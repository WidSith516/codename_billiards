# Install the Android toolchain (JDK + Android SDK) into godot/.tmp/, which .gitignore already covers.
#
# WHY A SCRIPT RATHER THAN A FEW INLINE COMMANDS: this is ~1.5 GB across several archives and it has to
# survive being run unattended. Each step verifies what it produced, because a half-installed SDK fails
# much later with a confusing message ("Could not find platform-tools") rather than at the point of error.
$ErrorActionPreference = "Stop"

$root = "D:\codename\codename_billiards\godot\.tmp\android"
$dl = Join-Path $root "dl"
New-Item -ItemType Directory -Force -Path $root, $dl | Out-Null

function Step($msg) { Write-Output "==> $msg" }

# ---------------------------------------------------------------- 1. JDK 17
# Godot's Android export needs a JDK; 17 is the version its gradle template targets. Temurin is used
# because it is a plain zip of a verified build, no installer and no registry.
$jdkDir = Join-Path $root "jdk"
if (Test-Path (Join-Path $jdkDir "bin\java.exe")) {
    Step "JDK already present"
} else {
    $jdkZip = Join-Path $dl "jdk17.zip"
    if (-not (Test-Path $jdkZip)) {
        $url = "https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jdk/hotspot/normal/eclipse"
        Step "downloading JDK 17"
        Invoke-WebRequest -Uri $url -OutFile $jdkZip -UseBasicParsing
    }
    Step "JDK zip = $((Get-Item $jdkZip).Length) bytes"
    $tmp = Join-Path $root "jdk_ex"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -Path $jdkZip -DestinationPath $tmp -Force
    # The archive contains a single jdk-17.x.y+z directory; flatten it so the path is stable.
    $inner = Get-ChildItem $tmp -Directory | Select-Object -First 1
    Move-Item $inner.FullName $jdkDir
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$javaExe = Join-Path $jdkDir "bin\java.exe"
if (-not (Test-Path $javaExe)) { throw "JDK install failed: no $javaExe" }
# `java -version` writes to STDERR. With $ErrorActionPreference = "Stop", redirecting that stream into
# the success stream turns each line into a terminating NativeCommandError, so the script aborted around
# a command that had actually succeeded. Relax the preference around external calls and judge them by
# their EXIT CODE instead -- stderr is not a failure signal for these tools.
function Invoke-Quiet([scriptblock]$block) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $block } finally { $ErrorActionPreference = $prev }
}
Step ("java: " + (Invoke-Quiet { (& $javaExe -version 2>&1 | Select-Object -First 1) | Out-String }).Trim())

# ---------------------------------------------------------------- 2. Android command-line tools
# Only the command-line tools are needed: sdkmanager pulls exactly the packages we name, instead of the
# multi-GB Android Studio bundle that includes an IDE, emulator images and unrelated platforms.
$sdkDir = Join-Path $root "sdk"
$cmdlineDir = Join-Path $sdkDir "cmdline-tools\latest"
if (Test-Path (Join-Path $cmdlineDir "bin\sdkmanager.bat")) {
    Step "cmdline-tools already present"
} else {
    $clZip = Join-Path $dl "cmdline-tools.zip"
    if (-not (Test-Path $clZip)) {
        $u = "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip"
        Step "downloading Android command-line tools"
        Invoke-WebRequest -Uri $u -OutFile $clZip -UseBasicParsing
    }
    Step "cmdline-tools zip = $((Get-Item $clZip).Length) bytes"
    $tmp = Join-Path $root "cl_ex"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive -Path $clZip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force -Path (Split-Path $cmdlineDir) | Out-Null
    Move-Item (Join-Path $tmp "cmdline-tools") $cmdlineDir
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$sdkmanager = Join-Path $cmdlineDir "bin\sdkmanager.bat"
if (-not (Test-Path $sdkmanager)) { throw "cmdline-tools install failed: no $sdkmanager" }

# ---------------------------------------------------------------- 3. SDK packages
# build-tools + platform-tools are the two Godot's export preset asks for; the NDK is NOT needed because
# this is a C# project -- the .NET runtime ships as an assembly, not a native build.
$env:JAVA_HOME = $jdkDir
$env:ANDROID_HOME = $sdkDir
$env:ANDROID_SDK_ROOT = $sdkDir

$packages = @("platform-tools", "platforms;android-34", "build-tools;34.0.0")
Step "accepting licences and installing: $($packages -join ', ')"
# sdkmanager prompts for each licence; feeding it 'y' repeatedly is the documented non-interactive way.
# Run it through Invoke-Quiet for the same reason as java: these tools write progress to stderr, and
# stderr must not be mistaken for failure.
$yes = ("y`n" * 30)
Invoke-Quiet { $yes | & $sdkmanager --sdk_root="$sdkDir" --licenses 2>&1 | Select-Object -Last 3 }
Invoke-Quiet { $yes | & $sdkmanager --sdk_root="$sdkDir" @packages 2>&1 | Select-Object -Last 10 }

# ---------------------------------------------------------------- 4. verify
Step "verifying"
$adb = Join-Path $sdkDir "platform-tools\adb.exe"
foreach ($p in @($adb, (Join-Path $sdkDir "build-tools\34.0.0\apksigner.bat"))) {
    Write-Output ("  " + $(if (Test-Path $p) { "OK   $p" } else { "MISS $p" }))
}

# Write the paths out, so the caller never has to guess where this landed.
$envFile = Join-Path $root "env.ps1"
@"
# Generated by tools/install_android_toolchain.ps1 -- source this to export for Android.
`$env:JAVA_HOME = "$jdkDir"
`$env:ANDROID_HOME = "$sdkDir"
`$env:ANDROID_SDK_ROOT = "$sdkDir"
`$env:PATH = "$jdkDir\bin;$sdkDir\platform-tools;" + `$env:PATH
"@ | Set-Content -Path $envFile -Encoding UTF8
Step "wrote $envFile"
Step "DONE"
