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
	p4n labels -e ($prefix + '*') | sort -prop @{ Expression = {
		[int]::parse([regex]::match($_.label, '(\d+)$').groups[1])
	}} | %{
		"$($_.label) - $($_.description.trim())"
	}
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
