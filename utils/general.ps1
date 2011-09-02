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
