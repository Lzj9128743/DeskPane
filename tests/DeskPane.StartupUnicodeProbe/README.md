# Startup Unicode integration probe

This opt-in Windows probe compiles the production task reader and backend with
DeskPane's actual UTF-8 application manifest. It checks Unicode task names and
paths, the registration contract, snapshot restoration, missing tasks, and both
STA and MTA callers against the real Task Scheduler.

The probe creates one disabled task with a unique `DeskPane Unicode Probe-` name
and removes it in `finally`. It never enables, overwrites, or deletes the user's
DeskPane startup task, and does not launch DeskPane. Run it as an ordinary user.

From the repository root:

```powershell
dotnet run --project .\tests\DeskPane.StartupUnicodeProbe\DeskPane.StartupUnicodeProbe.csproj
```

To check Native AOT, use an x64 Visual Studio developer shell:

```powershell
dotnet publish .\tests\DeskPane.StartupUnicodeProbe\DeskPane.StartupUnicodeProbe.csproj -c Release -p:Platform=x64 -r win-x64 -p:PublishAot=true -p:IlcUseEnvironmentalTools=true
& .\tests\DeskPane.StartupUnicodeProbe\bin\x64\Release\net10.0-windows\win-x64\publish\DeskPane.StartupUnicodeProbe.exe
```

Run the generated executable directly, so the process uses DeskPane's manifest.
The Native AOT executable can also be copied to a Windows 10 machine without
installing the .NET SDK. A successful probe does not replace a real login/startup
test of the application on that machine.
