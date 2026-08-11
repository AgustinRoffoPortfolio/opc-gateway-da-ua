// Punto de entrada del gateway. Por ahora solo verifica el bitness del proceso:
// el driver OPC DA exige 32 bits y esto tiene que fallar ruidosamente si algun
// dia alguien saca el PlatformTarget del csproj.
using System.Runtime.InteropServices;

Console.WriteLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Is64BitProcess: {Environment.Is64BitProcess}");