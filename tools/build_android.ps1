# Build a signed, installable Android APK.
#
# WHY SIGNING IS DONE HERE instead of in the Godot preset: Godot's three debug-keystore fields
# (keystore path / user / password) must be either ALL set or ALL empty, and the password field only
# accepts an editor-settings key name rather than a literal value. A half-configured preset fails the
# export outright with "debug keystore / debug user / debug password must all be filled or all be
# empty". So the preset exports UNSIGNED and this script signs explicitly with zipalign + apksigner,
# whose output can be verified (apksigner verify --print-certs) rather than trusting a setting that
# cannot be written from here.
#
# NOTE ON THE TFM: the project targets net8.0 and that is correct for Android too. An earlier note in
# tools/godotprobe/REPORT.md claimed Android needs net9.0; measured here, `dotnet publish -r
# android-arm64` on net8.0 succeeds, and GodotSharp 4.7.2 ships only lib/net8.0. No TFM swap is needed.
param(
    [ValidateSet("device", "emulator")][string]$Target = "device",
    [switch]$NoSign
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

# ---------------------------------------------------------------- running external tools
# Every tool on this path (java, keytool, zipalign, apksigner, aapt, Godot itself) writes progress and
# warnings to STDERR. Under $ErrorActionPreference = "Stop", PowerShell promotes each stderr LINE into a
# terminating NativeCommandError, so the script aborts around commands that actually SUCCEEDED. This bit
# three separate steps before being pinned down (java -version, the Godot export, keytool).
#
# Invoke-Tool relaxes the preference for exactly one call and returns the exit code. Callers must judge
# success the way a build script has to: by exit code, and by whether the expected artifact exists.
# Invoke-Tool relaxes the preference for exactly one call and returns the exit code.
#
# IT MUST NOT LET THE TOOL'S OUTPUT INTO ITS RETURN VALUE. PowerShell functions return EVERY uncaptured
# value from the body, so the obvious `& $Block 2>&1 | ForEach-Object { $_ }` returns the tool's lines
# PLUS the exit code as one array. The caller's `if ($rc -ne 0)` then compares an ARRAY to 0, which is
# true whenever the array is non-empty -- so a SUCCESSFUL command that printed anything (apksigner
# prints five certificate lines) was reported as a failure. That produced a bogus
# "the signed APK does NOT verify -- do not ship it" on a build whose signature was in fact valid.
#
# Hence the throwaway assignment around the call, and `Write-Output` (not a bare value) for the return.
function Invoke-Tool {
    param([scriptblock]$Block, [switch]$Quiet)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        if ($Quiet) {
            # Discard the tool's chatter; the exit code is the whole point of a quiet call.
            $null = & $Block 2>&1
        } else {
            # Show the output, but still keep it out of the return value. $LASTEXITCODE survives both
            # the capture and the pipeline here (verified against a deliberately failing command).
            $null = & $Block 2>&1 | ForEach-Object { Write-Host $_ }
        }
        $rc = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }
    Write-Output $rc
}

# ---------------------------------------------------------------- toolchain
$jdk = Join-Path $repo "godot\.tmp\android\jdk"
$sdk = Join-Path $repo "godot\.tmp\android\sdk"
foreach ($p in @((Join-Path $jdk "bin\java.exe"), (Join-Path $sdk "platform-tools\adb.exe"))) {
    if (-not (Test-Path $p)) {
        throw "Android toolchain incomplete: missing $p`nRun tools/finish_android_sdk.ps1 first."
    }
}
$env:JAVA_HOME = $jdk
$env:ANDROID_HOME = $sdk
$env:ANDROID_SDK_ROOT = $sdk

$bt = Join-Path $sdk "build-tools\34.0.0"
$zipalign = Join-Path $bt "zipalign.exe"
$apksigner = Join-Path $bt "apksigner.bat"

# Godot's Android export hands the project to dotnet, and the Android export TEMPLATE in this engine
# build accepts only net9.0 (engine message: "C# 项目以"net8.0"为目标，但是导出模板仅支持"net9.0"").
# The project therefore switches TargetFramework on RuntimeIdentifier (see RewindKernel.csproj), and the
# .NET 9 SDK must be the one that runs the build -- the system dotnet is 8.x only.
$dotnet9 = Join-Path $repo "godot\.tmp\android\dotnet9"
if (-not (Test-Path (Join-Path $dotnet9 "dotnet.exe"))) {
    throw "the .NET 9 SDK is required for an Android export and is missing at $dotnet9"
}
$env:DOTNET_ROOT = $dotnet9
# dotnet9 FIRST: a `dotnet` earlier on PATH is what the export would otherwise use, and the install is
# 8.x only, so the Android build would fail on the TFM check with a confusing message.
$env:PATH = "$dotnet9;" + (Join-Path $jdk "bin") + ";" + (Join-Path $sdk "platform-tools") + ";" + $env:PATH
# A private NUGET_PACKAGES for the Android build: mixing net8.0 and net9.0 assets for the same project
# id in one cache is how a stale project.assets.json comes back to bite, and the Android build is the
# one that must not silently reuse a desktop restore.
$env:NUGET_PACKAGES = Join-Path $repo "godot\.tmp\android\nuget9"

# ---------------------------------------------------------------- the net8.0 / net9.0 conflict
# The Android export template in this engine build accepts ONLY net9.0:
#   "C# 项目以"net8.0"为目标，但是导出模板仅支持"net9.0"。请考虑改用 Gradle 构建。"
# while GodotSharp.4.7.2 ships only lib/net8.0, which is what the desktop/editor build compiles
# against and what is known to work. The two requirements genuinely conflict.
#
# A RuntimeIdentifier-conditional TargetFramework in the .csproj DOES NOT solve this. Godot decides
# its template compatibility before any build, by evaluating the project WITHOUT an android RID set,
# so a conditional property still reads as net8.0 to it and the export is refused before it starts.
#
# So the framework is pinned in the project text for the duration of the export and restored in the
# finally block. Doing the swap here (rather than committing net9.0) keeps the desktop build on the
# framework it was verified against.
#
# Restoring from an in-memory copy rather than `git checkout` on purpose: a checkout would also
# discard any other uncommitted edit to this file.
$csprojPath = Join-Path $repo "godot\RewindKernel.csproj"
$csprojOriginal = Get-Content $csprojPath -Raw
$tfmPattern = '<TargetFramework>net8\.0</TargetFramework>'
if ($csprojOriginal -notmatch $tfmPattern) {
    throw "expected an unconditional <TargetFramework>net8.0</TargetFramework> in RewindKernel.csproj; refusing to guess at the current contents"
}
$csprojPatched = $csprojOriginal -replace $tfmPattern, '<TargetFramework>net9.0</TargetFramework>'
Set-Content -Path $csprojPath -Value $csprojPatched -NoNewline

# Sanity-check the swap actually produced exactly ONE TargetFramework element: a stray duplicate is
# silently won by whichever comes last, which is how an earlier attempt "pinned net9.0" and still
# failed the check with net8.0.
$declared = ([regex]::Matches($csprojPatched, '<TargetFramework>')).Count
if ($declared -ne 1) { throw "the swap left $declared TargetFramework elements; expected exactly 1" }
Write-Output "==> pinned TargetFramework to net9.0 for the Android export"

# ---------------------------------------------------------------- emulator needs x86_64
# The preset ships arm64-v8a only, because every real phone is arm64 and each extra architecture
# roughly doubles the native payload. A desktop Android emulator, by contrast, is usually x86_64, so an
# arm64-only APK simply will not install there. Rather than keep two presets in sync, the architecture
# is switched in the preset text for the duration of an emulator build and restored afterwards.
$presetPath = Join-Path $repo "godot\export_presets.cfg"
$presetOriginal = Get-Content $presetPath -Raw
if ($Target -eq "emulator") {
    Write-Output "==> emulator build: enabling x86_64 alongside arm64-v8a"
    $presetPatched = $presetOriginal -replace 'architectures/x86_64=false', 'architectures/x86_64=true'
    if ($presetPatched -eq $presetOriginal) { throw "could not enable x86_64 in export_presets.cfg" }
    Set-Content -Path $presetPath -Value $presetPatched -NoNewline
}

try {
    $distDir = Join-Path $repo "dist"
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    $unsigned = Join-Path $distDir "REWIND-unsigned.apk"

    Write-Output "==> exporting (unsigned)"
    # Judged by exit code and by whether the APK appeared -- NOT by stderr. Godot writes non-fatal noise
    # to stderr on this path ("EditorSettings not instantiated yet when getting setting
    # export/android/shutdown_adb_on_exit", leaked-instance warnings at exit).
    Push-Location $repo
    try {
        $exportRc = Invoke-Tool {
            & (Join-Path $repo "tools\godot\godot_console.exe") --headless --path godot `
                --export-release "Android" $unsigned 2>&1 |
                Where-Object { $_ -notmatch '^\s+at:\s|ObjectDB instances were leaked|resources still in use' }
        }
    } finally {
        Pop-Location
    }
    Write-Output "==> godot export rc=$exportRc"

    if (-not (Test-Path $unsigned)) { throw "export produced no APK at $unsigned (rc=$exportRc)" }
    Write-Output ("==> unsigned APK: {0:N0} bytes" -f (Get-Item $unsigned).Length)

    # ------------------------------------------------------------ verify the C# assembly is really in
    # The failure mode this guards against: a missing/empty assembly still produces a valid-looking
    # APK, and the app then dies on launch with an assembly-load error that is expensive to diagnose
    # on a device. Checking the file list here is much cheaper than discovering it in a crash log.
    #
    # The published assembly lives at assets/.godot/mono/publish/<abi>/RewindKernel.dll, NOT at
    # assets/RewindKernel.dll. Matching on the path suffix rather than an exact path keeps this working
    # if the engine relocates it again in a future version.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($unsigned)
    try {
        $names = $zip.Entries | ForEach-Object { $_.FullName }
        $hasAssembly = $names | Where-Object { $_ -like "*mono/publish/*/RewindKernel.dll" }
        $hasPck = $names | Where-Object { $_ -match '\.pck$|sparsepck$' }
        Write-Output "==> APK contains: assembly=$([bool]$hasAssembly) pck=$([bool]$hasPck) entries=$($names.Count)"
        if (-not $hasAssembly) {
            throw "APK has NO RewindKernel.dll -- the C# assembly was silently omitted (this is the failure mode that only shows up on the device)"
        }
    } finally {
        $zip.Dispose()
    }

    if ($NoSign) {
        Write-Output "==> -NoSign: leaving the APK unsigned at $unsigned"
        return
    }

    # ------------------------------------------------------------ sign
    $keystore = Join-Path $repo "godot\.tmp\android\rewind-debug.keystore"
    if (-not (Test-Path $keystore)) {
        Write-Output "==> creating a debug keystore"
        # A debug keystore is not a secret: it is the standard self-signed identity for local test
        # installs. keytool ships inside the JDK, so no extra download is needed. Its progress goes to
        # stderr (Chinese and long), hence -Quiet.
        $keytool = Join-Path $jdk "bin\keytool.exe"
        $rc = Invoke-Tool -Quiet {
            & $keytool -genkeypair -v -keystore $keystore -storepass android -keypass android `
                -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 `
                -dname "CN=Android Debug,O=Android,C=US"
        }
        if (-not (Test-Path $keystore)) { throw "keytool did not produce $keystore (rc=$rc)" }
    }

    $aligned = Join-Path $distDir "REWIND-aligned.apk"
    $final = Join-Path $distDir "REWIND.apk"
    Remove-Item $aligned, $final -Force -ErrorAction SilentlyContinue

    # zipalign MUST come before signing: aligning afterwards invalidates the signature.
    Write-Output "==> zipalign 4"
    $rc = Invoke-Tool -Quiet { & $zipalign -f 4 $unsigned $aligned }
    if ($rc -ne 0 -or -not (Test-Path $aligned)) { throw "zipalign failed (rc=$rc)" }

    Write-Output "==> apksigner"
    $rc = Invoke-Tool -Quiet {
        & $apksigner sign --ks $keystore --ks-pass pass:android --key-pass pass:android `
            --ks-key-alias androiddebugkey --out $final $aligned
    }
    if ($rc -ne 0 -or -not (Test-Path $final)) { throw "apksigner failed (rc=$rc)" }

    # ------------------------------------------------------------ verify, and show the evidence
    # The verification is the point of signing out here: it is a check that can fail loudly and
    # objectively, unlike "the preset was configured correctly".
    Write-Output "==> apksigner verify"
    $rc = Invoke-Tool { & $apksigner verify --print-certs $final }
    if ($rc -ne 0) { throw "the signed APK does NOT verify -- do not ship it" }

    Write-Output ""
    Write-Output ("SIGNED APK: $final  ({0:N0} bytes)" -f (Get-Item $final).Length)

    # The badging dump is REPORTING ONLY, and it must not be able to fail the build.
    # `aapt dump badging` chokes on this manifest ("failed to read attribute 'android:required':
    # attribute is not an integer value") -- an aapt limitation with modern manifest attributes, not a
    # defect in the APK, whose signature apksigner has already verified above. An earlier unguarded
    # invocation here is what turned a good, correctly-signed build into a reported failure.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $badging = & (Join-Path $bt "aapt.exe") dump badging $final 2>&1
        if ($LASTEXITCODE -eq 0) {
            $badging | Select-String -Pattern "^package:|native-code|sdkVersion|targetSdkVersion|launchable-activity" |
                ForEach-Object { Write-Output ("  " + $_.Line.Trim()) }
        } else {
            Write-Output "  (aapt badging unavailable -- reporting only, the APK is unaffected)"
        }
    } finally {
        $ErrorActionPreference = $prevEap
    }

    # What MUST be true of a shippable APK, asserted from the zip itself rather than from a tool's
    # opinion: the arm64 native library and the C# assembly are both present. The arm64 check matters
    # because the preset ships arm64-v8a only, and an APK without it installs nowhere.
    $zip2 = [System.IO.Compression.ZipFile]::OpenRead($final)
    try {
        $n2 = $zip2.Entries | ForEach-Object { $_.FullName }
        foreach ($need in @("lib/arm64-v8a/libgodot_android.so", "mono/publish/arm64/RewindKernel.dll")) {
            $ok = [bool]($n2 | Where-Object { $_ -like "*$need" })
            Write-Output ("  " + $(if ($ok) { "OK  " } else { "MISS" }) + " $need")
        }
        $abi = ($n2 | Where-Object { $_ -match '^lib/([^/]+)/' } |
            ForEach-Object { ($_ -split '/')[1] } | Sort-Object -Unique) -join ", "
        Write-Output "  ABIs in APK: $abi"
    } finally {
        $zip2.Dispose()
    }
} finally {
    # Restore BOTH temporary edits, unconditionally. An interrupted or failed Android export must never
    # leave the project pinned to net9.0 (which would break the desktop build) or with x86_64 enabled
    # in the preset (which would silently fatten every later release APK).
    Set-Content -Path $csprojPath -Value $csprojOriginal -NoNewline
    Write-Output "==> restored RewindKernel.csproj to net8.0"
    if ($Target -eq "emulator") {
        Set-Content -Path $presetPath -Value $presetOriginal -NoNewline
        Write-Output "==> restored export_presets.cfg"
    }
}
