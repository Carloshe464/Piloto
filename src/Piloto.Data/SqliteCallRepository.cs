using System.Globalization;
using Microsoft.Data.Sqlite;
using Piloto.Core.Abstractions;
using Piloto.Core.Configuration;
using Piloto.Core.Models;

namespace Piloto.Data;

/// <summary>
/// Repositório SQLite com fila persistida e busca FTS5. Uma única conexão de longa duração
/// protegida por lock (SQLite tem um único escritor; a fila já processa uma por vez).
/// </summary>
public sealed class SqliteCallRepository : ICallRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly object _lock = new();
    private SqliteConnection? _conn;

    public SqliteCallRepository(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(settings.CaminhoBanco);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = settings.CaminhoBanco,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    private SqliteConnection Conn => _conn ?? throw new InvalidOperationException("Repositório não inicializado. Chame Inicializar().");

    public void Inicializar()
    {
        lock (_lock)
        {
            _conn = new SqliteConnection(_connectionString);
            _conn.Open();
            using (var pragma = _conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }
            MigrationRunner.Aplicar(_conn);
        }
    }

    // ---------------------------------------------------------------- Fila

    public long EnfileirarItem(QueueItem item)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO queue (audio_atendente, audio_cliente, metadata_json, estado, tentativas, criado_em, registro_id)
                VALUES ($aa, $ac, $meta, $estado, 0, $criado, $reg);
                """;
            cmd.Parameters.AddWithValue("$aa", item.CaminhoAudioAtendente);
            cmd.Parameters.AddWithValue("$ac", item.CaminhoAudioCliente);
            cmd.Parameters.AddWithValue("$meta", (object?)item.MetadataJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$estado", (int)item.Estado);
            cmd.Parameters.AddWithValue("$criado", Iso(item.CriadoEm));
            // Reprocessamento: item já nasce apontando para o registro que vai atualizar.
            cmd.Parameters.AddWithValue("$reg", (object?)item.RegistroId ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            var id = UltimoId(null);
            item.Id = id;
            return id;
        }
    }

    public QueueItem? ProximoPendente()
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, audio_atendente, audio_cliente, metadata_json, estado, tentativas, ultimo_erro, criado_em, atualizado_em, registro_id
                FROM queue WHERE estado = $pendente ORDER BY id ASC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$pendente", (int)QueueState.Pendente);
            using var r = cmd.ExecuteReader();
            return r.Read() ? LerQueueItem(r) : null;
        }
    }

    public void AtualizarItem(QueueItem item)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                UPDATE queue SET estado=$estado, tentativas=$tent, ultimo_erro=$erro,
                    atualizado_em=$atualizado, registro_id=$reg
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$estado", (int)item.Estado);
            cmd.Parameters.AddWithValue("$tent", item.Tentativas);
            cmd.Parameters.AddWithValue("$erro", (object?)item.UltimoErro ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$atualizado", Iso(item.AtualizadoEm ?? DateTimeOffset.Now));
            cmd.Parameters.AddWithValue("$reg", (object?)item.RegistroId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.ExecuteNonQuery();
        }
    }

    public int ContarPendentes()
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM queue WHERE estado=$p;";
            cmd.Parameters.AddWithValue("$p", (int)QueueState.Pendente);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int RecuperarItensOrfaos(int maxTentativas)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            // A queda conta como tentativa: no CASE, "tentativas" ainda é o valor antigo.
            cmd.CommandText = """
                UPDATE queue SET
                    tentativas = tentativas + 1,
                    ultimo_erro = $motivo,
                    estado = CASE WHEN tentativas + 1 >= $max THEN $erro ELSE $pendente END,
                    atualizado_em = $agora
                WHERE estado = $processando;
                """;
            cmd.Parameters.AddWithValue("$motivo", "Processo encerrado inesperadamente durante o processamento");
            cmd.Parameters.AddWithValue("$max", maxTentativas);
            cmd.Parameters.AddWithValue("$erro", (int)QueueState.Erro);
            cmd.Parameters.AddWithValue("$pendente", (int)QueueState.Pendente);
            cmd.Parameters.AddWithValue("$agora", Iso(DateTimeOffset.Now));
            cmd.Parameters.AddWithValue("$processando", (int)QueueState.Processando);
            return cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- Registros

    public long SalvarRegistro(CallRecord r)
    {
        lock (_lock)
        {
            using var tx = Conn.BeginTransaction();
            long id;
            using (var cmd = Conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO calls
                      (uuid, numero, ticket, status_zendesk, atendente, iniciada_em, encerrada_em, criado_em,
                       duracao_seg, tempo_falado_seg, audio_atendente, audio_cliente,
                       transcript_json, transcript_texto, campos_json, resumo_json, resumo_texto,
                       motivo, produto, status_resumo, precisa_revisao, motivos_revisao_json,
                       email_cliente, telefone_cliente, nome_cliente)
                    VALUES
                      ($uuid, $numero, $ticket, $statusz, $atendente, $ini, $fim, $criado,
                       $dur, $falado, $aa, $ac,
                       $tjson, $ttexto, $cjson, $rjson, $rtexto,
                       $motivo, $produto, $statusr, $revisao, $motivosrev,
                       $emailc, $telc, $nomec);
                    """;
                var texto = r.Transcript.TextoRotulado();
                var resumoTexto = ResumoParaTexto(r);
                cmd.Parameters.AddWithValue("$uuid", r.Uuid);
                cmd.Parameters.AddWithValue("$numero", (object?)r.Metadata.Numero ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ticket", (object?)r.Metadata.TicketId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$statusz", (object?)r.Metadata.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$atendente", (object?)r.Metadata.Atendente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ini", (object?)(r.Metadata.IniciadaEm is { } i ? Iso(i) : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fim", (object?)(r.Metadata.EncerradaEm is { } f ? Iso(f) : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$criado", Iso(r.CriadoEm));
                cmd.Parameters.AddWithValue("$dur", r.Duracao.TotalSeconds);
                cmd.Parameters.AddWithValue("$falado", r.TempoFalado.TotalSeconds);
                cmd.Parameters.AddWithValue("$aa", (object?)r.CaminhoAudioAtendente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ac", (object?)r.CaminhoAudioCliente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tjson", CallSerialization.SerializarTranscript(r.Transcript));
                cmd.Parameters.AddWithValue("$ttexto", texto);
                cmd.Parameters.AddWithValue("$cjson", CallSerialization.Serializar(r.Campos));
                cmd.Parameters.AddWithValue("$rjson", CallSerialization.Serializar(r.Resumo));
                cmd.Parameters.AddWithValue("$rtexto", resumoTexto);
                cmd.Parameters.AddWithValue("$motivo", (object?)r.Resumo.MotivoContato ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$produto", (object?)r.Resumo.Produto ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$statusr", (object?)r.Resumo.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$revisao", r.PrecisaRevisao ? 1 : 0);
                cmd.Parameters.AddWithValue("$motivosrev", CallSerialization.Serializar(r.MotivosRevisao));
                cmd.Parameters.AddWithValue("$emailc", (object?)r.Metadata.EmailCliente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$telc", (object?)r.Metadata.TelefoneCliente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$nomec", (object?)r.Metadata.NomeCliente ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            id = UltimoId(tx);

            using (var fts = Conn.CreateCommand())
            {
                fts.Transaction = tx;
                fts.CommandText = """
                    INSERT INTO calls_fts (rowid, transcript_texto, resumo_texto, numero, ticket)
                    VALUES ($id, $ttexto, $rtexto, $numero, $ticket);
                    """;
                fts.Parameters.AddWithValue("$id", id);
                fts.Parameters.AddWithValue("$ttexto", r.Transcript.TextoRotulado());
                fts.Parameters.AddWithValue("$rtexto", ResumoParaTexto(r));
                fts.Parameters.AddWithValue("$numero", (object?)r.Metadata.Numero ?? DBNull.Value);
                fts.Parameters.AddWithValue("$ticket", (object?)r.Metadata.TicketId ?? DBNull.Value);
                fts.ExecuteNonQuery();
            }

            tx.Commit();
            r.Id = id;
            return id;
        }
    }

    public void AtualizarRegistro(CallRecord r)
    {
        lock (_lock)
        {
            using var tx = Conn.BeginTransaction();
            using (var cmd = Conn.CreateCommand())
            {
                cmd.Transaction = tx;
                // uuid e criado_em ficam intactos: é a mesma ligação, com conteúdo novo.
                cmd.CommandText = """
                    UPDATE calls SET
                        numero=$numero, ticket=$ticket, status_zendesk=$statusz, atendente=$atendente,
                        iniciada_em=$ini, encerrada_em=$fim,
                        duracao_seg=$dur, tempo_falado_seg=$falado,
                        audio_atendente=$aa, audio_cliente=$ac,
                        transcript_json=$tjson, transcript_texto=$ttexto, campos_json=$cjson,
                        resumo_json=$rjson, resumo_texto=$rtexto,
                        motivo=$motivo, produto=$produto, status_resumo=$statusr,
                        precisa_revisao=$revisao, motivos_revisao_json=$motivosrev,
                        email_cliente=$emailc, telefone_cliente=$telc, nome_cliente=$nomec
                    WHERE id=$id;
                    """;
                cmd.Parameters.AddWithValue("$id", r.Id);
                cmd.Parameters.AddWithValue("$numero", (object?)r.Metadata.Numero ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ticket", (object?)r.Metadata.TicketId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$statusz", (object?)r.Metadata.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$atendente", (object?)r.Metadata.Atendente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ini", (object?)(r.Metadata.IniciadaEm is { } i ? Iso(i) : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$fim", (object?)(r.Metadata.EncerradaEm is { } f ? Iso(f) : null) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$dur", r.Duracao.TotalSeconds);
                cmd.Parameters.AddWithValue("$falado", r.TempoFalado.TotalSeconds);
                cmd.Parameters.AddWithValue("$aa", (object?)r.CaminhoAudioAtendente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ac", (object?)r.CaminhoAudioCliente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tjson", CallSerialization.SerializarTranscript(r.Transcript));
                cmd.Parameters.AddWithValue("$ttexto", r.Transcript.TextoRotulado());
                cmd.Parameters.AddWithValue("$cjson", CallSerialization.Serializar(r.Campos));
                cmd.Parameters.AddWithValue("$rjson", CallSerialization.Serializar(r.Resumo));
                cmd.Parameters.AddWithValue("$rtexto", ResumoParaTexto(r));
                cmd.Parameters.AddWithValue("$motivo", (object?)r.Resumo.MotivoContato ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$produto", (object?)r.Resumo.Produto ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$statusr", (object?)r.Resumo.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$revisao", r.PrecisaRevisao ? 1 : 0);
                cmd.Parameters.AddWithValue("$motivosrev", CallSerialization.Serializar(r.MotivosRevisao));
                cmd.Parameters.AddWithValue("$emailc", (object?)r.Metadata.EmailCliente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$telc", (object?)r.Metadata.TelefoneCliente ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$nomec", (object?)r.Metadata.NomeCliente ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            using (var del = Conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM calls_fts WHERE rowid=$id;";
                del.Parameters.AddWithValue("$id", r.Id);
                del.ExecuteNonQuery();
            }
            using (var fts = Conn.CreateCommand())
            {
                fts.Transaction = tx;
                fts.CommandText = """
                    INSERT INTO calls_fts (rowid, transcript_texto, resumo_texto, numero, ticket)
                    VALUES ($id, $ttexto, $rtexto, $numero, $ticket);
                    """;
                fts.Parameters.AddWithValue("$id", r.Id);
                fts.Parameters.AddWithValue("$ttexto", r.Transcript.TextoRotulado());
                fts.Parameters.AddWithValue("$rtexto", ResumoParaTexto(r));
                fts.Parameters.AddWithValue("$numero", (object?)r.Metadata.Numero ?? DBNull.Value);
                fts.Parameters.AddWithValue("$ticket", (object?)r.Metadata.TicketId ?? DBNull.Value);
                fts.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }


    public CallRecord? ObterRegistro(long id)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = SelectCalls + " WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? LerCallRecord(r) : null;
        }
    }

    public IReadOnlyList<CallRecord> ListarRegistros(int limite = 200, int offset = 0)
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = SelectCalls + " ORDER BY id DESC LIMIT $lim OFFSET $off;";
            cmd.Parameters.AddWithValue("$lim", limite);
            cmd.Parameters.AddWithValue("$off", offset);
            using var r = cmd.ExecuteReader();
            var lista = new List<CallRecord>();
            while (r.Read()) lista.Add(LerCallRecord(r));
            return lista;
        }
    }

    public IReadOnlyList<CallRecord> Buscar(string termo, int limite = 200)
    {
        var query = MontarQueryFts(termo);
        if (query is null) return ListarRegistros(limite);

        lock (_lock)
        {
            List<long> ids = new();
            using (var cmd = Conn.CreateCommand())
            {
                cmd.CommandText = "SELECT rowid FROM calls_fts WHERE calls_fts MATCH $q ORDER BY rank LIMIT $lim;";
                cmd.Parameters.AddWithValue("$q", query);
                cmd.Parameters.AddWithValue("$lim", limite);
                using var r = cmd.ExecuteReader();
                while (r.Read()) ids.Add(r.GetInt64(0));
            }
            if (ids.Count == 0) return Array.Empty<CallRecord>();

            var resultado = new List<CallRecord>(ids.Count);
            foreach (var id in ids)
            {
                using var cmd = Conn.CreateCommand();
                cmd.CommandText = SelectCalls + " WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (r.Read()) resultado.Add(LerCallRecord(r));
            }
            return resultado;
        }
    }

    // ---------------------------------------------------------------- Retenção

    public RetencaoResultado AplicarRetencao(int diasAudio, int diasTranscricao)
    {
        lock (_lock)
        {
            var cutoffAudio = Iso(DateTimeOffset.Now.AddDays(-diasAudio));
            var cutoffTransc = Iso(DateTimeOffset.Now.AddDays(-diasTranscricao));
            var audiosRemovidos = 0;
            var registrosRemovidos = 0;

            // 1) Apaga áudios de chamadas antigas, mas preserva a transcrição.
            var pares = new List<(long Id, string? A, string? C)>();
            using (var sel = Conn.CreateCommand())
            {
                sel.CommandText = """
                    SELECT id, audio_atendente, audio_cliente FROM calls
                    WHERE criado_em < $cut AND (audio_atendente IS NOT NULL OR audio_cliente IS NOT NULL);
                    """;
                sel.Parameters.AddWithValue("$cut", cutoffAudio);
                using var r = sel.ExecuteReader();
                while (r.Read())
                    pares.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }
            foreach (var (id, a, c) in pares)
            {
                audiosRemovidos += ApagarArquivo(a);
                audiosRemovidos += ApagarArquivo(c);
                using var upd = Conn.CreateCommand();
                upd.CommandText = "UPDATE calls SET audio_atendente=NULL, audio_cliente=NULL WHERE id=$id;";
                upd.Parameters.AddWithValue("$id", id);
                upd.ExecuteNonQuery();
            }

            // 2) Remove por completo registros muito antigos (transcrição + FTS + áudio residual).
            var idsAntigos = new List<(long Id, string? A, string? C)>();
            using (var sel = Conn.CreateCommand())
            {
                sel.CommandText = "SELECT id, audio_atendente, audio_cliente FROM calls WHERE criado_em < $cut;";
                sel.Parameters.AddWithValue("$cut", cutoffTransc);
                using var r = sel.ExecuteReader();
                while (r.Read())
                    idsAntigos.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }
            foreach (var (id, a, c) in idsAntigos)
            {
                ApagarArquivo(a);
                ApagarArquivo(c);
                using var tx = Conn.BeginTransaction();
                using (var d1 = Conn.CreateCommand())
                {
                    d1.Transaction = tx;
                    d1.CommandText = "DELETE FROM calls_fts WHERE rowid=$id;";
                    d1.Parameters.AddWithValue("$id", id);
                    d1.ExecuteNonQuery();
                }
                using (var d2 = Conn.CreateCommand())
                {
                    d2.Transaction = tx;
                    d2.CommandText = "DELETE FROM calls WHERE id=$id;";
                    d2.Parameters.AddWithValue("$id", id);
                    d2.ExecuteNonQuery();
                }
                tx.Commit();
                registrosRemovidos++;
            }

            return new RetencaoResultado(audiosRemovidos, registrosRemovidos);
        }
    }

    public ContadoresGerais Contadores()
    {
        lock (_lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(tempo_falado_seg), 0),
                       COALESCE(SUM(CASE WHEN precisa_revisao=1 THEN 1 ELSE 0 END), 0)
                FROM calls;
                """;
            using var r = cmd.ExecuteReader();
            r.Read();
            return new ContadoresGerais(
                r.GetInt32(0),
                TimeSpan.FromSeconds(r.GetDouble(1)),
                r.GetInt32(2));
        }
    }

    // ---------------------------------------------------------------- Helpers

    private const string SelectCalls = """
        SELECT id, uuid, numero, ticket, status_zendesk, atendente, iniciada_em, encerrada_em, criado_em,
               duracao_seg, tempo_falado_seg, audio_atendente, audio_cliente,
               transcript_json, campos_json, resumo_json, precisa_revisao, motivos_revisao_json,
               email_cliente, telefone_cliente, nome_cliente
        FROM calls
        """;

    private long UltimoId(SqliteTransaction? tx)
    {
        using var cmd = Conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = "SELECT last_insert_rowid();";
        return (long)cmd.ExecuteScalar()!;
    }

    private static QueueItem LerQueueItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        CaminhoAudioAtendente = r.GetString(1),
        CaminhoAudioCliente = r.GetString(2),
        MetadataJson = r.IsDBNull(3) ? null : r.GetString(3),
        Estado = (QueueState)r.GetInt32(4),
        Tentativas = r.GetInt32(5),
        UltimoErro = r.IsDBNull(6) ? null : r.GetString(6),
        CriadoEm = ParseDto(r.GetString(7)),
        AtualizadoEm = r.IsDBNull(8) ? null : ParseDto(r.GetString(8)),
        RegistroId = r.IsDBNull(9) ? null : r.GetInt64(9),
    };

    private static CallRecord LerCallRecord(SqliteDataReader r)
    {
        var registro = new CallRecord
        {
            Id = r.GetInt64(0),
            Uuid = r.GetString(1),
            Metadata = new CallMetadata
            {
                Numero = r.IsDBNull(2) ? null : r.GetString(2),
                TicketId = r.IsDBNull(3) ? null : r.GetString(3),
                Status = r.IsDBNull(4) ? null : r.GetString(4),
                Atendente = r.IsDBNull(5) ? null : r.GetString(5),
                IniciadaEm = r.IsDBNull(6) ? null : ParseDto(r.GetString(6)),
                EncerradaEm = r.IsDBNull(7) ? null : ParseDto(r.GetString(7)),
                EmailCliente = r.IsDBNull(18) ? null : r.GetString(18),
                TelefoneCliente = r.IsDBNull(19) ? null : r.GetString(19),
                NomeCliente = r.IsDBNull(20) ? null : r.GetString(20),
            },
            CriadoEm = ParseDto(r.GetString(8)),
            Duracao = TimeSpan.FromSeconds(r.GetDouble(9)),
            TempoFalado = TimeSpan.FromSeconds(r.GetDouble(10)),
            CaminhoAudioAtendente = r.IsDBNull(11) ? null : r.GetString(11),
            CaminhoAudioCliente = r.IsDBNull(12) ? null : r.GetString(12),
            Transcript = CallSerialization.DeserializarTranscript(r.IsDBNull(13) ? null : r.GetString(13)),
            Campos = CallSerialization.Deserializar(r.IsDBNull(14) ? null : r.GetString(14), ObjectiveFields.Vazio()),
            Resumo = CallSerialization.Deserializar(r.IsDBNull(15) ? null : r.GetString(15), LlmSummary.Vazio()),
            PrecisaRevisao = r.GetInt32(16) == 1,
            MotivosRevisao = CallSerialization.Deserializar(r.IsDBNull(17) ? null : r.GetString(17), new List<string>()),
        };
        return registro;
    }

    private static string ResumoParaTexto(CallRecord r)
    {
        var partes = new[] { r.Resumo.Resumo, r.Resumo.MotivoContato, r.Resumo.Produto, r.Resumo.Status, r.Resumo.Pedido, r.Resumo.ProximoPasso };
        return string.Join(' ', partes.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static int ApagarArquivo(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho)) return 0;
        try { File.Delete(caminho); return 1; }
        catch { return 0; }
    }

    /// <summary>Monta uma consulta FTS5 de prefixo segura a partir do termo do usuário.</summary>
    internal static string? MontarQueryFts(string termo)
    {
        if (string.IsNullOrWhiteSpace(termo)) return null;
        var tokens = termo
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(c => char.IsLetterOrDigit(c) || c is '@' or '.' or '_' or '-').ToArray()))
            .Where(t => t.Length > 0)
            .Select(t => t + "*");
        var query = string.Join(' ', tokens);
        return string.IsNullOrWhiteSpace(query) ? null : query;
    }

    private static string Iso(DateTimeOffset dto) => dto.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDto(string s)
        => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose()
    {
        lock (_lock)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }
}
