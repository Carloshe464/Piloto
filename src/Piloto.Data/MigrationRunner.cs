using Microsoft.Data.Sqlite;

namespace Piloto.Data;

/// <summary>
/// Migrações incrementais versionadas por <c>PRAGMA user_version</c>. Cada entrada da lista
/// é aplicada uma única vez, em ordem. A FTS5 usa <c>unicode61 remove_diacritics 2</c> para
/// que a busca ignore acentos.
/// </summary>
internal static class MigrationRunner
{
    private static readonly string[] Migracoes =
    {
        // v1 — esquema inicial
        """
        CREATE TABLE IF NOT EXISTS queue (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            audio_atendente TEXT NOT NULL,
            audio_cliente   TEXT NOT NULL,
            metadata_json   TEXT,
            estado          INTEGER NOT NULL DEFAULT 0,
            tentativas      INTEGER NOT NULL DEFAULT 0,
            ultimo_erro     TEXT,
            criado_em       TEXT NOT NULL,
            atualizado_em   TEXT,
            registro_id     INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_queue_estado ON queue(estado);

        CREATE TABLE IF NOT EXISTS calls (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            uuid                TEXT NOT NULL,
            numero              TEXT,
            ticket              TEXT,
            status_zendesk      TEXT,
            atendente           TEXT,
            iniciada_em         TEXT,
            encerrada_em        TEXT,
            criado_em           TEXT NOT NULL,
            duracao_seg         REAL NOT NULL DEFAULT 0,
            tempo_falado_seg    REAL NOT NULL DEFAULT 0,
            audio_atendente     TEXT,
            audio_cliente       TEXT,
            transcript_json     TEXT,
            transcript_texto    TEXT,
            campos_json         TEXT,
            resumo_json         TEXT,
            resumo_texto        TEXT,
            motivo              TEXT,
            produto             TEXT,
            status_resumo       TEXT,
            precisa_revisao     INTEGER NOT NULL DEFAULT 0,
            motivos_revisao_json TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_calls_criado ON calls(criado_em);
        CREATE INDEX IF NOT EXISTS ix_calls_revisao ON calls(precisa_revisao);

        CREATE VIRTUAL TABLE IF NOT EXISTS calls_fts USING fts5(
            transcript_texto,
            resumo_texto,
            numero,
            ticket,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """,

        // v2 — contato do solicitante lido do cadastro do Zendesk pela extensão.
        // Registros antigos ficam com NULL: o app trata como "não informado".
        """
        ALTER TABLE calls ADD COLUMN email_cliente    TEXT;
        ALTER TABLE calls ADD COLUMN telefone_cliente TEXT;
        ALTER TABLE calls ADD COLUMN nome_cliente     TEXT;
        """,
    };

    public static void Aplicar(SqliteConnection conn)
    {
        long versaoAtual;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            versaoAtual = Convert.ToInt64(cmd.ExecuteScalar());
        }

        for (var v = (int)versaoAtual; v < Migracoes.Length; v++)
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Migracoes[v];
                cmd.ExecuteNonQuery();
            }
            using (var bump = conn.CreateCommand())
            {
                bump.Transaction = tx;
                // PRAGMA não aceita parâmetro; o valor vem de índice interno controlado.
                bump.CommandText = $"PRAGMA user_version = {v + 1};";
                bump.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
