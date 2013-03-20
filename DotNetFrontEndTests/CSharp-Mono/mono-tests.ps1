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


Param(
  [string]$daikon_jar="C:\Projects\contract-inserter\daikon\daikon.jar"
)


$MONO_TEST_DIR = 'C:\Projects\mono-tests-debug'
$CSC_EXE = { C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /warn:0 /unsafe /debug /out:$testexe $f }
$CSC_EXE_DRIVER = { C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /warn:0 /unsafe /debug /r:TestDriver.dll /out:$testexe $f }
$DNFE_EXE = { .\DotNetFrontEndLauncher.exe --save-program=instrumented.exe $testexe }
$PEVERIFY_EXE = { C:\Program Files\Microsoft SDKs\Windows\v8.0A\bin\NETFX 4.0 Tools\PEVerify.exe instrumented.exe }

cd $MONO_TEST_DIR
$files = ls *.cs

rm daikon-output\*.dtrace

if (!(Test-Path skip.txt)){
    New-Item skip.txt -Type file
}
if (!(Test-Path no-run.txt)){
    New-Item no-run.txt -Type file
}
if (!(Test-Path no-samples.txt)){
    New-Item no-samples.txt -Type file
}

foreach($f in $files)
{
    $result = ""

    $test = $f.BaseName;
    $testexe = $test, "exe" -join "."

    # Results Caching Files
    $passed = $f.Name, "passed" -join "."
    $failed = $f.Name, "failed" -join "."
    $crashed = $f.Name, "crash" -join "."
    $cantcompile = $f.Name, "cant-compile" -join "."
    $norewrite = $f.Name, "norewrite" -join "."
    $norun = $f.Name, "norun" -join "."
    $daikonfailed = $f.Name, "daikon-failed" -join "."
   

    if (Test-Path $passed){
        echo "$test PASSED";
        continue    
    } elseif (Select-String skip.txt -Pattern $test -Quiet){
        echo "$test SKIPPED";
        continue    
    } elseif (Test-Path $crashed){
        echo "$test CRASH";
        continue    
    } elseif (Test-Path $failed){
        echo "$test FAIL";
        continue  
    } elseif (Test-Path $norewrite){
        echo "$test CANTREWRITE";
        continue
    } elseif (Test-Path $norun){
        echo "$test SKIPRUN";
        continue
    } elseif (Test-Path $daikonfailed){
        echo "$test CANTDAIKON";
        continue
    }
    elseif (Test-Path $cantcompile){
        echo "$test CANTCOMPILE";
        continue
    }

    # Perform Cleanup

    if (Test-Path $testexe){
        Remove-Item $testexe -Force -Recurse 
    }
    if (Test-Path instrumented.exe){
        Remove-Item instrumented.exe -Force -Recurse
    }

     echo "$test..." 

    ## Compile the Test

    if (Select-String $f -Pattern "TestDriver." -Quiet){
        $result = & $CSC_EXE_DRIVER 2>&1
    }else{
        $result = & $CSC_EXE 2>&1
    }

    if (!(Test-Path $testexe))
    {
        echo "$test CANTCOMPILE";
        New-Item $cantcompile -ItemType file | Out-Null
        Set-Content $cantcompile -Value $result
        continue;
    }

   

    ## Rewrite the Binary
    try{
        $result = & $DNFE_EXE 2>&1
        $nopass = $false
    }catch{
        $nopass = $true
    }

    if (($LASTEXITCODE -ne 0) -or $nopass)
    {
        echo "$test INSTRUMENT FAILED";
        #Remove-Item $testexe -Force
        New-Item $norewrite -ItemType file | Out-Null
        Set-Content $norewrite -Value $result
        continue
    }

    ## Verify the Binary
    $result = & $PEVERIFY_EXE 2>&1
    
    if ($LASTEXITCODE -ne 0)
    {
        echo "$f VERIFY FAILED";
        Remove-Item $testexe -Force
        Remove-Item instrumented.exe -Force
        New-Item $norewrite -ItemType file | Out-Null
        Set-Content $norewrite -Value $result
        continue;
    }

    if (!(Test-Path instrumented.exe)){
        echo "ERROR: missing instrumented.exe after 'successful' rewrite";
        continue;
    }
    
    ## Complete Test if NO-RUN file is present
    if (Select-String no-run.txt -Pattern $test -Quiet){
        New-Item $norun -ItemType file | Out-Null
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
        New-Item $crashed -ItemType file | Out-Null
        echo "$f PASSED (Uninstrumented Program Crashes)";
        Remove-Item $testexe -Force
        Remove-Item instrumented.exe -Force 
        continue
    }
        
    if (!(Test-Path instrumented.exe)){
        echo "ERROR: missing instrumented.exe after 'successful' rewriting";
        continue
    }


    if (Test-Path daikon-output\$test.dtrace){
        Remove-Item daikon-output\$test.dtrace
    }

    ## Run the instrumented binary
    Remove-Item $testexe -Force
    Move-Item instrumented.exe $testexe

    try{
        $result = ""
        & ".\$testexe" # 2>&1 
        $nopass = $false
    }catch{
        $nopass = $true
    }

    if (($LASTEXITCODE -ne 0) -or $nopass)
    {
        echo "$f EXECUTION FAILED";
        New-Item $failed -ItemType file | Out-Null
        Set-Content $failed -Value $result
        #Remove-Item $testexe -Force
        continue
    }
   
    $DAIKON_EXE = { java -Xmx1024M -cp $daikon_jar daikon.Daikon daikon-output\$test.decls daikon-output\$test.*dtrace }
    $result = & $DAIKON_EXE 2>&1 

  
    if ($LASTEXITCODE -ne 0)
    {
        if ((Select-String no-samples.txt -Pattern $test -Quiet) -and (Select-String -pattern "No samples found for any" -inputobject $result)){
            New-Item $passed -ItemType file | Out-Null
            echo "$f PASSED";
        }
        else
        {
            echo "$f DAIKON FAILED";
            New-Item $daikonfailed -ItemType file | Out-Null
            Set-Content $daikonfailed -Value $result
            #Remove-Item $testexe -Force
            continue
        }
    }
    else
    {
        New-Item $passed -ItemType file | Out-Null
        echo "$f PASSED";
    }

    
}
