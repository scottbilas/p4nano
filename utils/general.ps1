<### Format helpers ###>

function Output-String {
    process { $_.tostring() }
}

set-alias ostr output-string

function Output-FormString {
    process { $_.toformstring() }
}

function ConvertDateTime-P4ToSystem($p4date) {
    return [p4nano.utility]::p4tosystem($p4date)
}

function ConvertDateTime-SystemToP4($datetime) {
    return [p4nano.utility]::systemtop4($datetime)
}

<### Useful stuff ###>

function p4n-delete-emptychangelists {
	p4n changes -s pending -c ((p4n info).clientname) | sort {$_.change} | %{p4n describe ($_.change)} |
		?{ !$_.shelved -and !$_.arrayfields['depotfile'].count } |
		%{ p4n change -d $_.change }
}

function p4n-unshelve {

    [CmdletBinding(DefaultParameterSetName='BugID', SupportsShouldProcess=$true)]
    param(
        [int]$ChangeList,
        [switch]$NoCheckout,
        [switch]$IncludeUnmapped
        #$AltRoot
    )

    if (!$NoCheckout) {
        throw 'NoCheckout is the only mode supported currently'
    }

    foreach ($file in (p4n describe -s -S $ChangeList|% items|%{ p4n where $_.depotfile })) {

        if (!$IncludeUnmapped -and $file.unmap) { continue }

        if ((test-path $file.path) -and !$PSCmdlet.ShouldContinue("Overwrite $($file.path)?", 'Confirm Overwrite')) {
            continue
        }

        Write-Host ('{0} to {1}' -f (?: $NoCheckout 'Copying' 'Unshelving'), $file.path)

        if (!$WhatIfPreference) {
            p4 print -q -o $file.path "$($file.depotfile)@=$ChangeList"
        }
    }
}
