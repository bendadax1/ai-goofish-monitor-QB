using System.Security.Cryptography;

namespace AiGoofish.Launcher.Platform.Windows;

public sealed class InstanceSecrets
{
    internal InstanceSecrets(
        string postgresBootstrapAdminPassword,
        string applicationDatabasePassword,
        string probeDatabasePassword,
        string encryptionMasterKey,
        string secretKey)
    {
        PostgresBootstrapAdminPassword = postgresBootstrapAdminPassword;
        ApplicationDatabasePassword = applicationDatabasePassword;
        ProbeDatabasePassword = probeDatabasePassword;
        EncryptionMasterKey = encryptionMasterKey;
        SecretKey = secretKey;
        Validate();
    }

    public string PostgresBootstrapAdminPassword { get; }

    public string ApplicationDatabasePassword { get; }

    public string ProbeDatabasePassword { get; }

    public string EncryptionMasterKey { get; }

    public string SecretKey { get; }

    internal static InstanceSecrets Generate()
    {
        return new InstanceSecrets(
            GenerateValue(),
            GenerateValue(),
            GenerateValue(),
            GenerateValue(),
            GenerateValue());
    }

    internal IEnumerable<string> Values()
    {
        yield return PostgresBootstrapAdminPassword;
        yield return ApplicationDatabasePassword;
        yield return ProbeDatabasePassword;
        yield return EncryptionMasterKey;
        yield return SecretKey;
    }

    public override string ToString() => "InstanceSecrets([REDACTED])";

    private static string GenerateValue()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void Validate()
    {
        ValidateValue(PostgresBootstrapAdminPassword, allowUrlSafe: false);
        ValidateValue(ApplicationDatabasePassword, allowUrlSafe: true);
        ValidateValue(ProbeDatabasePassword, allowUrlSafe: true);
        ValidateValue(EncryptionMasterKey, allowUrlSafe: false);
        ValidateValue(SecretKey, allowUrlSafe: false);
    }

    private static void ValidateValue(string value, bool allowUrlSafe)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InstanceSecretsFormatException("受保护凭据字段缺失或为空。");
        }

        byte[] decoded;
        try
        {
            var encoded = value;
            if (allowUrlSafe && value.IndexOfAny(['-', '_']) >= 0)
            {
                if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
                {
                    throw new FormatException();
                }

                encoded = value.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            }

            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InstanceSecretsFormatException("受保护凭据字段不是有效的 Base64。", exception);
        }

        try
        {
            if (decoded.Length < 32)
            {
                throw new InstanceSecretsFormatException("受保护凭据字段少于 32 字节。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }
}

public class InstanceSecretsException : InvalidOperationException
{
    public InstanceSecretsException(string message)
        : base(message)
    {
    }

    public InstanceSecretsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class InstanceSecretsFormatException : InstanceSecretsException
{
    public InstanceSecretsFormatException(string message)
        : base(message)
    {
    }

    public InstanceSecretsFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class InstanceSecretsAlreadyExistException : InstanceSecretsException
{
    public InstanceSecretsAlreadyExistException()
        : base("实例基础凭据文件已存在，拒绝覆盖或重新生成。")
    {
    }
}

public sealed class InstanceSecretsProtectionException : InstanceSecretsException
{
    public InstanceSecretsProtectionException(string operation, int nativeErrorCode)
        : base($"Windows 当前用户数据保护在{operation}时失败；错误码 0x{nativeErrorCode:X8}。")
    {
        NativeErrorCode = nativeErrorCode;
    }

    public int NativeErrorCode { get; }
}
