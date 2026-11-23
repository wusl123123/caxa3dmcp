@echo off
rem MCP 服务端启动器：用已编译的 DLL 运行（依赖本机 .NET 8 运行时）。
rem 若需免运行时独立 exe，先：dotnet publish -c Release -r win-x64，再改为调用 caxa3dmcpserver.exe
dotnet "%~dp0bin\Release\net8.0\caxa3dmcpserver.dll"
