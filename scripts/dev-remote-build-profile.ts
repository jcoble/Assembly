// This unit runs dotnet Assembly.Remote.dll on Linux x64; it needs neither other native platforms nor an apphost.
export const remotePublishArguments = [
  '-c', 'Release', '--no-self-contained', '-r', 'linux-x64', '-p:UseAppHost=false', '-p:UseSharedCompilation=false',
  // Dev launch uses compiler diagnostics; the normal commit/CI checks own the full analyzer pass.
  '-p:RunAnalyzersDuringBuild=false',
];
