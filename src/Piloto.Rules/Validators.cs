using Piloto.Core.Text;

namespace Piloto.Rules;

/// <summary>Validadores de dígito verificador e afins, usados para calibrar a confiança.</summary>
public static class Validators
{
    /// <summary>Valida um CPF pelos dois dígitos verificadores.</summary>
    public static bool CpfValido(string entrada)
    {
        var cpf = TextUtils.SomenteDigitos(entrada);
        if (cpf.Length != 11) return false;

        // Rejeita sequências repetidas (000..., 111...), que passam no cálculo mas são inválidas.
        if (cpf.Distinct().Count() == 1) return false;

        int Digito(int ate, int pesoInicial)
        {
            var soma = 0;
            for (var i = 0; i < ate; i++)
                soma += (cpf[i] - '0') * (pesoInicial - i);
            var resto = soma % 11;
            return resto < 2 ? 0 : 11 - resto;
        }

        var d1 = Digito(9, 10);
        var d2 = Digito(10, 11);
        return d1 == (cpf[9] - '0') && d2 == (cpf[10] - '0');
    }

    /// <summary>Valida um CNPJ pelos dois dígitos verificadores (módulo 11, pesos 2..9 cíclicos).</summary>
    public static bool CnpjValido(string entrada)
    {
        var cnpj = TextUtils.SomenteDigitos(entrada);
        if (cnpj.Length != 14) return false;
        if (cnpj.Distinct().Count() == 1) return false;

        int Digito(int ate)
        {
            var soma = 0;
            var peso = 2;
            for (var i = ate - 1; i >= 0; i--)
            {
                soma += (cnpj[i] - '0') * peso;
                peso = peso == 9 ? 2 : peso + 1;
            }
            var resto = soma % 11;
            return resto < 2 ? 0 : 11 - resto;
        }

        return Digito(12) == (cnpj[12] - '0') && Digito(13) == (cnpj[13] - '0');
    }
}
