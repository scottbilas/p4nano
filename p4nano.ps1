##### REMOVE ?? and ?: USAGE

# TODO:
# 
# have p4 changes automatically include the -l flag - no point in truncating


# enable this when working on p4nano.cs
#$p4nanodebug = $true
$p4nanodebug = $false

<### Compile p4nano.cs ###>

$p4nanocp = new-object codedom.compiler.compilerparameters
$p4nanocp.ReferencedAssemblies.Add('system.dll') > $null
$p4nanocp.ReferencedAssemblies.Add('system.core.dll') > $null

# optionally turn on debugging support
if ($p4nanodebug) {
    # delete old unused crap while we're at it
    dir "$($env:temp)\p4nano-*.dll" | %{
        del $_ -ea silentlycontinue
        if ($?) { del $_.fullname.replace('.dll', '.pdb') -ea silentlycontinue }
    }
    $error.clear()

    $p4nanocp.TreatWarningsAsErrors = $true
    $p4nanocp.IncludeDebugInformation = $true
    $p4nanocp.OutputAssembly = $env:temp + '\p4nano-' + [diagnostics.process]::getcurrentprocess().id + '.dll'
}

# this is the only way i could figure out how to use c#3 with a .cs file
$p4nanoscript = [io.file]::readalltext((split-path $myinvocation.mycommand.definition) + '\p4nano.cs')
add-type $p4nanoscript -lang csharpversion3 -compilerparam $p4nanocp

<### Configure p4nano ###>

# this isn't working right now..have to figure out how to get posh to either pass a ctrl-c
# or dispose outstanding enumerables when it kills the script.
[p4nano.record]::CancelOnCtrlC = $true

<### Primary entry points ###>

filter p4-run {

    $input = $_

    $dotnet = $false
    $hasver = $false
    $fa = $null
    $ea = $null
    $wa = $null

    $cargs = [p4nano.commandargs]::parse($args)

    function getaction($a) {
        $action = $a.split('=', 2)[1]
        $rx = 'continue|ignore|throw'
        if ($action -notmatch $rx) {
            throw "Illegal action type '$action', must be one of $rx"
        }
        $action
    }

    # process pre-args to look for p4n-specific stuff
    for ($i = 0; $i -lt $cargs.preargs.count;) {
        $a = $cargs.preargs[$i]
        if ($a -cmatch '^-zapi') {
            $hasver = $true
            ++$i;
        }
        elseif ($a -ceq '-znet') {
            $dotnet = $true
            $cargs.preargs.removeat($i)
        }
        elseif ($a.startswith('-fa=')) {
            $fa = getaction $a
            $cargs.preargs.removeat($i)
        }
        elseif ($a.startswith('-ea=')) {
            $ea = getaction $a
            $cargs.preargs.removeat($i)
        }
        elseif ($a.startswith('-wa=')) {
            $wa = getaction $a
            $cargs.preargs.removeat($i)
        }
        else {
            ++$i;
        }
    }

    # wrap quotes around any args that have spaces
    for ($i = 0; $i -lt $cargs.postargs.count; ++$i) {
        [string]$a = $cargs.postargs[$i]
        if ($a -match ' ') {
            $cargs.postargs[$i] = "`"$a`""
        }
    }

    # lock to version 2010.1 if not being explicit. update if we use newer features of the protocol.
    if (!$hasver) {
        # http://kb.perforce.com/article/512/perforce-protocol-levels
        $cargs.preargs.insert(0, '-zapi=67')
    }

    # incoming records are used as input, otherwise they're the last arg
    if ($input -and ($input -isnot [p4nano.record])) {
        $cargs.postargs.add($input)
        $input = $null
    }

    $oldargs = $args
    $args = $cargs.allargs

    function report([bool]$iserror, [string]$action, [string]$message) {
        switch (?? $action $fa) {
            'ignore' { }
            'throw' { throw $message.trim() + " [from p4.exe $oldargs]" }
            default {
                if ($iserror) { write-error $message; write-error "with p4.exe $oldargs" }
                else { write-warning $message; write-warning "with p4.exe $oldargs" }
            }
        }
    }

    if ($dotnet) {
        # lazy eval seems to work well. the main problem with lazy is that if you do a sync or other non-readonly operation and do
        # not consume the entire enumerable before exit, then the operation will only partially complete. but it looks like
        # powershell does the enumerable walk when the user ignores (or sends to null) the return from p4n-run, so we're good.

        [p4nano.record]::run((pwd), $args, $input, $true) | %{

            # 'info' is tricky for a couple reasons:
            #
            #   1. it can arguably be warnings sometimes, such as when adding a file twice, or adding a nonexistent or directory file.
            #   2. we can't route it to write-output because it will get mixed in with the record stream. so...host instead?
            #
            # got a better idea of what to do with infos? it would be best to print them somehow so the warning-infos
            # don't get missed in a list of p4 commands.
            #
            #   - Buffer everything and print in the dispose for the enumerator?
            #   - Reinterpret infos as strings and drop the record from the stream?

            if ($_.isfailure) {
                if ($_.iswarning) { report $false $wa $_.data }
                else { report $true $ea $_.data }
            }
            ###elseif ($_.isinfo) { write-host $_.data } # see above notes..need to decide what to do with infos

            $_
        }
    }
    else {
        p4.exe $args
    }
}

filter p4n-run {
    # future: support input pipe for p4 args. collect args into groups of maybe 100 and run a command for each.
    # note that if the input is a Record object then this would be incompatible for simultaneous use with p4 args type input pipe.

    $a = ,'-znet' + $args
    $(ForEach ($ht in ($_ | p4-run @a)) { new-object PSObject -Property $ht }) # fixed by redpixel74. now can use Out-Gridview.
}

set-alias p4 p4-run
set-alias p4n p4n-run

filter P4N-Fix-TimeField ($fieldname = "Time")
{
	# Filter for converting p4 time field to datetime. Usage: P4N changes //depotpath/... | P4N-Fix-TimeField Time | Out-GridView
    $_ | Select-Object -ExcludeProperty $fieldname -Property *, @{Name="$fieldname"; Expression={ (Get-Date -Date "1970-01-01 00:00:00Z").AddSeconds($_.$fieldname) } }
}
