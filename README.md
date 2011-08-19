p4nano
======

p4nano is a lightweight .NET wrapper around Perforce's command line app p4.exe. It was written specifically for PowerShell but also works great in C#!

p4nano uses p4's -R option to output binary Ruby dictionaries, which makes the output from p4 fully parseable (unlike -ztag or text-based parsing which both have ambiguous cases and can be error-prone).


License
=======

p4nano is licensed under the Microsoft Public License (Ms-PL). See LICENSE.txt in the same package as p4nano for the complete details.


Disclaimer!
===========

While I have been building tools for Perforce since 2000 or so, I've only been working in PowerShell full time for a year or so. Still very much a n00b. Still have much to learn. p4nano will likely change over time as people school me on the correct way to design and doc posh commands.


Setup
=====

### General

Make sure p4.exe is on your path. Should already be the case, right?

### PowerShell

Just dot-source p4nano.ps1 to install the necessary functions. Don't worry about p4nano.cs, it is compiled automatically by p4nano.ps1. It just needs to be in the same folder as the .ps1.

### C# and VB.NET

Add p4nano.cs to a C# library and reference the assembly in. Or you could just add p4nano.cs directly to your project.


FAQ
===

_I'll add to this over time as I get questions._

### Q: Why wrap p4.exe instead of using p4.net?

**A: Several reasons!**

	1. I have used variants of p4.net for years and have always been unhappy with it. Inconsistent handling of edge cases, bugs, usage of ancient C# programming style, lack of maintenance...

	2. Relying on an underlying native library brings up annoying x86/x64 issues. ($$$Link to my web page)

	3. Making p4.net friendly to PowerShell is a big job. Too much rework was required.

### Q: Why Ms-PL?

**A: It's the best for me**

I did a survey of open source licenses and was astonished to find I liked Microsoft's the best. It had everything I wanted.

Most importantly: it's not a viral license, it requires no attribution, and it's very short. There is a near-zero burden on someone looking to use p4nano.
