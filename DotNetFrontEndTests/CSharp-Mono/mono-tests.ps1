# Tests Celeriac rewriting using the Mono tests from: https://github.com/mono/mono/tree/master/mono/tests
#
# 1. Download the tests from https://github.com/mono/mono/tree/master/mono/tests (i.e., download the repository ZIP)
# 2. Copy the DNFE files to the test directory
# 2.5 Copy the no-run.txt and skip.txt files to the test directory
# 3. Set the PATH variables
# 4. Run the script (currently, you have to watch the script to cancel out of the Celeriac "Has Stopped Working" dialog)

# The script caches results by writing a <test>.cs.passed or <test>.cs.failed file
# The script assumes that the unmodified program should run without crashing.

# TODO Celeriac crashes should be properly logged in .failed file
# TODO support for Mono tests that depend on the driver class

$MONO_TEST_DIR = 'C:\Projects\mono-tests'
$CSC_EXE = { C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /warn:0 /debug /out:$testexe $f }
$DNFE_EXE = { .\DotNetFrontEndLauncher.exe --testing-mode--save-program=instrumented.exe $testexe }
$PEVERIFY_EXE = { C:\Program Files\Microsoft SDKs\Windows\v8.0A\bin\NETFX 4.0 Tools\PEVerify.exe instrumented.exe }

cd $MONO_TEST_DIR
$files = ls *.cs

if (!(Test-Path skip.txt)){
    New-Item skip.txt -Type file
}
if (!(Test-Path no-run.txt)){
    New-Item no-run.txt -Type file
}

foreach($f in $files)
{
    $test = $f.BaseName;
    $testexe = $test, "exe" -join "."

    # Results Caching Files
    $passed = $f.Name, "passed" -join "."
    $failed = $f.Name, "failed" -join "."
    $crashed = $f.Name, "crash" -join "."
    
    if (Test-Path $passed){
        echo "$test ALREADY PASSED";
        continue    
    }

    if (Select-String skip.txt -Pattern $test -Quiet){
        echo "$test SKIPPED";
        continue    
    }

    if (Test-Path $crashed){
        echo "$test SKIPPED (Uninstrumented Program Crashes)";
        continue    
    }

    if (Test-Path $failed){
        echo "$test SKIPPED (FAILED)";
        continue  
    }

    echo "running test $test...";

    # Perform Cleanup

    if (Test-Path $testexe){
        Remove-Item $testexe -Force -Recurse 
    }
    if (Test-Path instrumented.exe){
        Remove-Item instrumented.exe -Force -Recurse
    }

    ## Compile the Test
    & $CSC_EXE 2>&1 | Out-Null

    if (!(Test-Path $testexe))
    {
        echo "$test SKIPPED (MS Compilation Error)";
        continue;
    }

    ## Rewrite the Binary
    try{
        & $DNFE_EXE 2>&1 | Out-Null
        $nopass = $false
    }catch{
        $nopass = $true
    }

    if (($LASTEXITCODE -ne 0) -or $nopass)
    {
        echo "$test INSTRUMENT FAILED";
        Remove-Item $testexe -Force
        New-Item $failed -ItemType file | Out-Null
        continue
    }

    ## Verify the Binary
    & $PEVERIFY_EXE 2>&1 | Out-Null
    
    if ($LASTEXITCODE -ne 0)
    {
        echo "$f VERIFY FAILED";
        Remove-Item $testexe -Force
        Remove-Item instrumented.exe -Force
        New-Item $failed -ItemType file | Out-Null
        continue;
    }
    
    ## Complete Test if NO-RUN file is present
    if (Select-String no-run.txt -Pattern $test -Quiet){
        New-Item $passed -ItemType file | Out-Null
        echo "$f PASSED (Skipped Run)";
        Remove-Item $testexe -Force
        Remove-Item instrumented.exe -Force 
        continue;
    }

    ## Run the Original File
    try{
       & ".\$testexe" 2>&1 | Out-Null
       $nopass = $false
    }catch{
       $nopass = $true
    }
        
    if (($LASTEXITCODE -ne 0) -or $nopasss)
    {
        New-Item $passed -ItemType file | Out-Null
        echo "$f PASSED (Uninstrumented Program Crashes)";
        Remove-Item $testexe -Force
        Remove-Item instrumented.exe -Force 
        continue;
    }
        
    ## Run the instrumented binary
    Remove-Item $testexe -Force
    Move-Item instrumented.exe $testexe

    try{
        $result = & ".\$testexe" 2>&1 
        $nopass = $false
    }catch{
        $nopass = $true
    }

    if (($LASTEXITCODE -ne 0) -or $nopass)
    {
        echo "$f EXECUTION FAILED";
        New-Item $failed -ItemType file | Out-Null
        Set-Content $failed -Value $result
    }
    else
    {
        New-Item $passed -ItemType file | Out-Null
        echo "$f PASSED";
    }

    Remove-Item $testexe -Force
    
}