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
