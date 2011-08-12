#define TESTCLIENT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using P4Nano;

class Test
{
	const string WorkingDir = @"C:\temp\p4sample\client";
	//const string WorkingDir = @"C:\temp\p4triggers\p4client";
	//const string WorkingDir = @"C:\proj\depot";

	static void Main()
	{
		//var c = new CommandArgs("-p perforce:1666 -u scott files -f foo //...@123,234");
		var c = new CommandArgs("-u def abc");

		var input = new Record();
		input["Change"] = "new";
		input["Description"] = "I'm a multi-\n line description.";
		P41("change -i", input);

		P41("set");

#if TESTCLIENT
		P41("client -d abc");
		var record = P41("client -o abc");
		var oldRecord = record.Clone();
		Debug.Assert(record == oldRecord);
		record.ArrayFields["View"].Add("//depot/x/... //" + record.Fields["Client"] + "/x/...");
		Debug.Assert(record != oldRecord);
		P41("client -i", record);
		var newRecord = P41("client -o abc");
		Debug.Assert(oldRecord != newRecord);
		Debug.Assert(newRecord != record);
		newRecord.Fields.Remove("Update");
		newRecord.Fields.Remove("Access");
		Debug.Assert(newRecord == record);
		P41("client -d abc");
#endif

		P4("sync #none");
		P4("sync");

		/*
		var root = P41("client -o")["Root"] + "\\_bigaddtest\\";
		if (Directory.Exists(root))
		{
			Directory.Delete(root, true);
		}

		var files = new List<string>();

		for (int i = 0; i < 10; ++i)
		{
			int jmax = new Random().Next(100);
			for (int j = 0; j < jmax; ++j)
			{
				var path = root + i + "\\" + j + "\\";
				Directory.CreateDirectory(path);

				int kmax = new Random().Next(50);
				for (int k = 0; k < kmax; ++k)
				{
					var filename = path + k + ".bin";
					files.Add(filename);
					using (var file = File.Create(filename))
					{
						file.SetLength(new Random().Next(1024 * 50));
					}
				}
			}
		}
		 * */

		//$$$$$P41("add", ...)

		/*P41("client -d abc", writeOutput: false);

		var client = P41("client -o abc", writeOutput: false);
		var view = client.ArrayFields["view"];
		view.Add("//depot/x/... //" + client["client"] + "/x/...");
		P41("client -i", client);
		P41("client -o abc");*/

		/*Record.CancelOnCtrlC = true;

		//var e = Record.Run(WorkingDir, "files //depot/engine/...");
		var e = Record.Run(WorkingDir, "filelog //...");
		int c = 0;
		foreach (var i in e)
		{
			Console.WriteLine(i);
			Thread.Sleep(500);
		}*/
	}

	static IEnumerable<Record> P4(string cmdLine, Record input = null, bool writeOutput = true)
	{
		var records = Record.Run(WorkingDir, cmdLine, input);
		if (writeOutput)
		{
			foreach (var record in records)
			{
				Console.WriteLine(record);
			}
		}
		return records;
	}

	static Record P41(string cmdLine, Record input = null, bool writeOutput = true)
	{
		var records = Record.Run(WorkingDir, cmdLine, input);
		foreach (var record in records)
		{
			if (writeOutput)
			{
				Console.WriteLine(record);
			}
			return record;
		}

		return null;
	}
}
