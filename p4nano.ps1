$p4nanodebug = $true

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

# this isn't working right now..have to figure out how to get posh to either pass a ctrl-c
# or dispose outstanding enumerables when it kills the script.
[p4nano.record]::CancelOnCtrlC = $true

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
                if ($iserror) { write-error $message }
                else { write-warning $message }
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
    $_ | p4-run @a
}

set-alias p4 p4-run
set-alias p4n p4n-run

function Output-String {
    process { $_.tostring() }
}

function Output-FormString {
    process { $_.toformstring() }
}

function ConvertDateTime-P4ToSystem($p4date) {
    return [p4nano.utility]::p4tosystem($p4date)
}

function ConvertDateTime-SystemToP4($datetime) {
    return [p4nano.utility]::systemtop4($datetime)
}

set-alias ostr output-string

function p4n-snapshot-create {
	$info = p4n info

	$latest = 0
	$prefix = $info.clientname + '_snapshot_'

	$labels = p4n labels -e ($prefix + '*')

	"Found $(?? $labels.count 0) existing labels created by user"

	$labels | ?{$_.label -match '\d+$'} | %{
	    $serial = [int]$matches[0]
	    if ($serial -ge $latest) {
	        $latest = $serial + 1
	    }
	}

	$l = new-object p4nano.record
	$l.Label = $prefix + $latest
	$l.Description = "Snapshot label created for $($info.username) on $(get-date)"
	$l.Options = 'unlocked'
	$l.Owner = $info.username
	$l.arrayfields.set('View', '//depot/...')

	"Created label $($l.label)"

	$l | p4n label -i > $null

	"Copying client state to label..."

	p4 labelsync -q -l $l.Label

	"Done!"
}

function p4n-snapshot-list {
	$info = p4n info

	$prefix = $info.clientname + '_snapshot_'
	p4n labels -e ($prefix + '*') | %{$_.label} | sort -prop @{ Expression = {
		[int]::parse([regex]::match($_, '(\d+)$').groups[1])
	}}
}

filter p4n-snapshot-delete(
	[parameter(Mandatory=$true,ValueFromPipeline=$true)]
	[int]$serial) {
    if ($_ -ne $null) { $serial = $_ }

	$info = p4n info

	$prefix = $info.clientname + '_snapshot_'
	(p4n label -d ($prefix + $serial)).data
}

filter p4n-snapshot-delete-old([int]$KeepCount = 5) {
	$snapshots = p4n-snapshot-list | %{[int]::parse([regex]::match($_, '(\d+)$').groups[1])}
	for ($i = 0; $i -lt $snapshots.count - $keepcount; ++$i) {
		p4n-snapshot-delete $snapshots[$i]
	}
}

function p4n-cdiff([int]$changenum) {

	# TODO future: detect when the have version == the 'new' version, the 'new' version is not checked out locally, and if so use the workstation path instead. that way can edit the file directly in bc.
	# TODO future: have an option to just diff everything vs. local copy
	# TODO future: have an option to ignore insignificant changes (will require examining diff2 output) like whitespace-only changes, 'using namespace'-only changes..
	# TODO future: have an option to batch them into sets of say 10 files per bcompare.exe instance. really not too useful when have a billion files and can't see which is which.
    # TODO future: accept path args to filter the changelist

	$status = 'pending'
	if ($changenum) { $status = (p4n change -o $changenum).status }

	if ($status -eq 'pending') {

	    $shelved = $false

	    if ($changenum) {
	        $files = p4n -nowarn fstat -Ro -e $changenum //...
	        # try shelved
	        if ($files.iserror) {
	            $files = p4n fstat -Rs -e $changenum //...
	            $shelved = $true
	        }
	    }
	    else {
	        $files = p4n fstat -Ro //...
	    }

		#####WIP...
        if (!$files.iserror) {
    	    "IMPLEMENT ME!!"
        }
        else {
            $files
        }
	}
	elseif ($status -eq 'submitted') {

	    $p4diff = $null
	    $isbc = $false

	    if ((p4 set p4diff) -match '=(.*?)(\s+\([^)]*\))?$') {
	        $p4diff = $matches[1]
	        if ((split-path -leaf $p4diff) -eq 'bcomp.exe') {
				$isbc = $true
				# switch to bcompare.exe to cut down on processes - we aren't waiting for bcomp.exe to quit
				$p4diff = (split-path $p4diff) + '\bcompare.exe'
	        }
	    }

	    (p4n describe -s $changenum).items | %{

	        $olddepot = "$($_.depotfile)#$($_.rev-1)"
	        $newdepot = "$($_.depotfile)#$($_.rev)"

            if ($_.action -eq 'add') {
                $olddepot = $null
            }
            elseif ($_.action -eq 'delete') {
                $newdepot = $null
            }
            elseif ($_.action -ne 'edit') {
                write-error "action type $($_.action) currently unsupported (for $($_.depotfile))"
                continue
            }

	        if ($p4diff) {

	            $tempname = [io.path]::gettemppath() + 'p4n\' + $_.depotfile.substring(2).replace('/', '\')
	            $path = split-path $tempname
	            $filename = split-path -leaf $tempname
	            $ext = [io.path]::getextension($filename)
	            $nameonly = [io.path]::getfilenamewithoutextension($filename)

	            $oldfile = "$path\$nameonly#$($_.rev-1)$ext"

                if ($olddepot) {
    	            $oldtitle = "$filename - $olddepot"
                    p4 print -q -o $oldfile $olddepot
                }
                else {
    	            $oldtitle = "$filename - (new file)"
                    if (test-path $oldfile) { del -force $oldfile }
                }

	            $newfile = "$path\$nameonly#$($_.rev)$ext"

                if ($newdepot) {
    	            $newtitle = "$filename - $newdepot"
	                p4 print -q -o $newfile $newdepot
                }
                else {
                    $newtitle = "$filename - (deleted file)"
                    if (test-path $newfile) { del -force $newfile }
                }

	            # special support for bc
                $diffargs = @()
	            if ($isbc) {
                    $diffargs += '/ro', "/title1=$oldtitle", "/title2=$newtitle"
	            }
                $diffargs += $oldfile
                if ($newdepot) {
                    $diffargs += $newfile
                }
                &$p4diff $diffargs

				$newdepot
	            sleep .1 # sleep a bit to let bcomp keep up, so the files stay in the order we want
	        }
	        else {
	            # fall back to meh behavior
	            p4 diff2 $olddepot $newdepot
	        }
	    }
	}
}
