#cd C:\temp\p4triggers
cd C:\temp\p4sample\client

. ((split-path $myinvocation.mycommand.definition) + '\..\p4nano.ps1')

#p4n infos

$c = p4n client -o
$c.update
#$c = $c | add-member -membertype scriptproperty -name Client -value {$this['client']} -passthru
#$c.client

return

p4n clients -e testy_test* | %{$_['client']} | p4n client -d

$c1 = p4n client -o testy_test
$c1.arrayfields['view'].add("//depot/x/... //$($c1['client'])/x/...")
$c1 | p4n client -i

$c2 = $c1.clone()
$c2['client'] = 'testy_test2'
$c2.arrayfields['view'].set("//depot/... //$($c2['client'])/...")
$c2 | p4n client -i

# note that building a form from scratch requires correct case (e.g. 'Client' instead of 'client')
3..10 | %{
    $c = new-object p4nano.record
    $c['Client'] = 'testy_test' + $_
    $c['Root'] = $env:temp
    $c
} | p4n client -i

#record.ArrayFields["View"].Add("//depot/x/... //" + record.Fields["Client"] + "/x/...");
#Debug.Assert(record != oldRecord);
#P41("client -i", record);
#var newRecord = P41("client -o abc");
#Debug.Assert(oldRecord != newRecord);
#Debug.Assert(newRecord != record);
#newRecord.Fields.Remove("Update");
#newRecord.Fields.Remove("Access");
#Debug.Assert(newRecord == record);
#P41("client -d abc");


#$client = p4n client -o abc
#$view = $client.arrayfields['view']
#$view.set(123, 456)
#$client.toformstring()

#$client = p4n client -o
#$client['client']
#$client.arrayfields['view']
#$client.arrayfields.set('abc', @(1,2,3))
#$client.arrayfields|%{$_.name}
#$client.arrayfields['abc'].add(5)
#$client.arrayfields['abc'].insert(0, 15)
#$client.arrayfields['abc']

#$changes = p4n describe -s 24
#$changes.arrayfields['depotfile'].insert(0, 'add')
#$changes.arrayfields['depotfile'].add('add')
#$changes.tostring()


#p4n files //depot/engine/...
#p4n submit -d big

#(p4n filelog //depot/main/shared/tools/max/functions/setCallbacksActive.ms) | select -first 1 | fstr

#p4n infos | p4n-write-errors

#### does not pipeline! loads all into memory first, sucky
#p4n files //...

### BUG WITH READLONG
#p4x filelog //depot/main/.../ai.cpp

#p4x files //depot/_global/...

#p4n -p 1666 add abc

<#
if($false)
{
$e = p4n filelog //...
foreach ($i in $e)
{
    $i
    sleep -Milliseconds 500
}
}

#p4n branch -o aa | p4n-write-errors -IncludeInfo
p4n client -o aa
#>
