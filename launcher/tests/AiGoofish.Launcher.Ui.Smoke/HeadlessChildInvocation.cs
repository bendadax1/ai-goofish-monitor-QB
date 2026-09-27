using System.Reflection;

internal static class HeadlessChildInvocation
{
    public static IReadOnlyList<string> BuildArguments(string processPath, string assemblyLocation, IReadOnlyList<string> childArguments)
    {
        var result = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(assemblyLocation) || !File.Exists(assemblyLocation))
            {
                throw new InvalidOperationException("无法确定 UI smoke 测试程序集路径，拒绝启动无界子进程。");
            }
            result.Add(assemblyLocation);
        }
        result.AddRange(childArguments);
        return result;
    }
}
