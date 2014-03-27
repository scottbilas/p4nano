function p4n-cdiff {
	[CmdletBinding(SupportsShouldProcess = $true)]
	param([int]$ChangeNum, [switch]$NoReviewFilter, [switch]$NoDiff, [switch]$SplitFolders, [string]$OutputPath)

	# TODO future: detect when the have version == the 'new' version, the 'new' version is not checked out locally, and if so use the workstation path instead. that way can edit the file directly in bc.
	# TODO future: have an option to just diff everything vs. local copy
	# TODO future: have an option to ignore insignificant changes (will require examining diff2 output) like whitespace-only changes, 'using namespace'-only changes..
	# TODO future: have an option to batch them into sets of say 10 files per bcompare.exe instance. really not too useful when have a billion files and can't see which is which.
    # TODO future: accept path args to filter the changelist

	if (!$NoReviewFilter) {
		$reviews = (p4n -ea=throw user -o).arrayfields['reviews'] | %{
		    if ($_[0] -eq '-') {
		        $_ = $_.substring(1)
		        $inclusive = $false
		    }
		    else {
		        $inclusive = $true
		    }

		    @{ rx = [p4nano.utility]::P4ToRegex($_); inclusive = $inclusive }
		}
	}

	function ShouldDiff($path) {
		if (!$reviews) { return $true }

		$include = $false
		$reviews | %{
		    if ($_.rx.ismatch($path)) {
		        $include = $_.inclusive
		    }
		}

		$include
	}

	$status = 'pending'
	if ($ChangeNum) { $status = (p4n -ea=throw change -o $ChangeNum).status }

	if ($status -eq 'pending') {

	    $shelved = $false

<#
WIP TESTING

$change = 557035 # remote
$change = 557037 # local

$files = @()

$pending = p4n -wa=ignore fstat -Ro -e $change //...
if ($pending[0].code -eq 'stat') {
    $files += $pending
}

$shelved = p4n -wa=ignore fstat -Rs -e $change //...
if ($shelved[0].code -eq 'stat') {
    $files += $shelved
}

$files | ?{ $_.depotfile } | %{ '{0}: ({1}) - {2}' -f $_.depotfile, (?: $_.shelved 'shelved' 'pending'), $_.code }

''

(p4n change -o $change).arrayfields
#>

	    if ($ChangeNum) {
	        $files = p4n -wa=ignore fstat -Ro -e $ChangeNum //...
	        # try shelved
	        if ($files.iserror) {
	            $files = p4n -ea=throw fstat -Rs -e $ChangeNum //...
	            $shelved = $true
	        }
	    }
	    else {
	        $files = p4n -ea=throw fstat -Ro //...
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

		$basepath = ?? $OutputPath ([io.path]::gettemppath() + 'p4n\')

		$changeinfo = p4n -ea=throw describe -s $ChangeNum
		$changepath = "{0}_{1}_({2})" -f $ChangeNum, $changeinfo.user, $changeinfo.items.count

        if ($changeinfo.path -match '//depot/([^/]+)/') {
            $changepath += '-' + $matches[1]
        }

		if (!$WhatIfPreference) {
			if (!(test-path $basepath)) { mkdir $basepath >$null }

			$describepath = $basepath

			if ($SplitFolders) {
				$describepath = join-path (join-path $basepath 'set0') $changepath
				if (!(test-path $describepath)) {
					mkdir $describepath > $null
				}
			}

			$shortdesc = $changeinfo.desc -replace "[\n*?:\\/\[\]`"><]", '_'
			if ($shortdesc.length -ge 60) {
				$shortdesc = $shortdesc.substring(0, 60)
			}

			# write out changelist description in case it's needed
			p4 describe -s $ChangeNum > "$describepath\$changenum - $shortdesc.txt"
		}

		$basepath = resolve-path $basepath

		# detect beyond compare
		$p4diff = (p4n set p4diff).items[0].value
	    $isbc = $p4diff -and (split-path -leaf $p4diff) -eq 'bcomp.exe'
		if ($isbc) {
			# switch to bcompare.exe to cut down on processes - we aren't waiting for bcomp.exe to quit
			$p4diff = (split-path $p4diff) + '\bcompare.exe'
	    }

		$ChangeNum

        $files = (p4n -ea=throw describe -s $ChangeNum).items | sort `
			{!$_.depotfile.tolower().endswith('.sln')},			# sln first
			{!$_.depotfile.tolower().endswith('.csproj')},		# then csproj
			{[io.path]::getextension($_.depotfile).tolower()},	# then group by extension
			{$_.depotfile.tolower()}							# finally sorted by filename

        $commonprefix = $null
        $files | %{
            $parts = $_.depotfile -split '/' | ?{ $_ }

            if ($commonprefix) {
                for ($i = 0; $i -lt $commonprefix.length; ++$i) {
                    if ($parts[$i] -ne $commonprefix[$i]) {
                        $commonprefix = $commonprefix[0..$i]
                    }
                }
            }
            else {
                $commonprefix = $parts[0..($parts.length-2)]
            }
        }

        $commonprefix = $commonprefix -join '/'
        if ($commonprefix) { $commonprefix += '/' }

        foreach ($_ in $files) {

	        $olddepot = "$($_.depotfile)#$($_.rev-1)"
	        $newdepot = "$($_.depotfile)#$($_.rev)"
			$doit = $true

			if (ShouldDiff $_.depotfile) {
	            if ($_.action -match '^(add|branch)$') {
	                $olddepot = $null
	            }
	            elseif ($_.action -eq 'delete') {
	                $newdepot = $null
	            }
	            elseif ($_.action -notmatch '^(edit|integrate|branch)$') {

					# $$$ implement integ and move support. it's not so simple because we still want the folder diff to work even though the names/locations may have changed.

	                write-warning "action type $($_.action) currently unsupported (for $($_.depotfile))"
	                $doit = $false
	            }
			}
			else {
	            write-output "-$($_.depotfile) - skipped"
				$doit = $false
			}

			if ($doit) {

				$relativepath = $_.depotfile.substring(2 + $commonprefix.length).replace('/', '\')

				if ($SplitFolders) {
			        $oldtempname = join-path $basepath (join-path "set0\$changepath" $relativepath)
			        $newtempname = join-path $basepath (join-path "set1\$changepath" $relativepath)

					$oldfile = $oldtempname
					$newfile = $newtempname
				}
				else {
			        $ext = [io.path]::getextension($_.depotfile)
			        $nameonly = [io.path]::getfilenamewithoutextension($_.depotfile)

			        $oldtempname = $newtempname = join-path $basepath $relativepath

			        $oldpath = split-path $oldtempname
			        $oldfile = "$oldpath\$nameonly#$($_.rev-1)$ext"
			        $newpath = split-path $newtempname
			        $newfile = "$newpath\$nameonly#$($_.rev)$ext"
				}

		        $oldfilename = split-path -leaf $oldtempname
		        $newfilename = split-path -leaf $newtempname

	            if ($olddepot) {
	    	        $oldtitle = "$oldfilename - $olddepot"
					if (!$WhatIfPreference) {
	                    p4 print -q -o $oldfile $olddepot
					}
	            }
	            else {
	    	        $oldtitle = "$oldfilename - (new file)"
	                if (test-path $oldfile) { del -force $oldfile }
	            }

	            if ($newdepot) {
	    	        $newtitle = "$newfilename - $newdepot"
					if (!$WhatIfPreference) {
			            p4 print -q -o $newfile $newdepot
					}
	            }
	            else {
	                $newtitle = "$newfilename - (deleted file)"
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

				if (!$WhatIfPreference -and !$NoDiff) {

			        if ($p4diff) {
		                &$p4diff $diffargs
				        sleep .1 # sleep a bit to let bcomp keep up, so the files stay in the order we want
					}
			        else {
			            # fall back to meh behavior
				        p4 diff2 $olddepot $newdepot
			        }
				}

				$newdepot
			}
	    }
	}
}
