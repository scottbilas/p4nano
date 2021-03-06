//#define DEBUG_SINGLE_RECORD_STREAM

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// TODO
//
// view parsing!! break a view down by splitting with the space and handling quotes
// put together neat examples with the p4 sample depot
// have standard set of names that auto convert to datetime ('time', 'accessed', 'updated') [??]
// what is the max # of params we can send in to p4.exe?
// cannot pass ctrl-c through to p4.exe. apparently ctrl-c is not getting our finally{} called. so we have dangling p4.exe's waiting to output more data. not totally sure about this yet.
// make some fun helpers like a ClientSpec or TypeMapSpec class
// auto login on fail (only if p4loginsso defined?)
// some kind of cleanup method i can call to nuke old p4.exe's that haven't exited normally?
// ok say you do a "p4n clients|%{super-expensive-command $_}". sometime during this loop the clients call appears to hang. timeout?
// it would be neat to be able to do something like this:
//		- p4n filelog (p4n opened -m1)
//   (...instead of this) p4n filelog (p4n opened -m1)['depotfile']
//   i.e. use context to determine if we want to pull a path out of an incoming record
// 'diff2' output (and many others like 'describe' that return diffs etc.) does not output diff records on -R or -ztag. Known issue with P4 support, my name is on the ticket. have to work around until implemented.
//		how does p4ruby handle? how about their new .net API?

// possible confusion points:
//
//   - not realizing that every "foreach" on a returned record set will queue up a new p4 command
//		- intuitive to think "Run = p4 command" but not true
//		- alternative is worse: foreach twice on a record set would not walk the same collection twice

// impl notes:
//
//   - when wanting to receive an enumerable of strings, do it as an enumerable of objects instead. or do both as overloads.
//     powershell doesn't automatically convert its own object array (which you can trivially get with ,'str') to a string enumerable.

// ReSharper disable EmptyGeneralCatchClause

namespace P4Nano
{
	public class ArrayFieldCollection : IEnumerable<ArrayField>
	{
		readonly Record _record;

		public ArrayFieldCollection(Record record) { _record = record; }

		public IEnumerable<string> ToStrings(int indent)
		{
			var indentText = new string(' ', indent * 4);
			foreach (var arrayField in this)
			{
				yield return indentText + arrayField.Name + ":";
				foreach (var str in arrayField.ToStrings(indent + 1))
				{
					yield return str;
				}
			}
		}

		public IEnumerable<string> ToStrings() { return ToStrings(0); }

		public override string ToString()
		{
			var sb = new StringBuilder();
			foreach (var str in ToStrings())
			{
				sb.AppendLine(str);
			}
			return sb.ToString();
		}

		public ArrayField this[string key] { get { return new ArrayField(_record, key); } }

		public void Set(string key, params object[] values) { Set(key, (IEnumerable<object>)values); }
		public void Set(string key, IEnumerable<object> values) { new ArrayField(_record, key).Set(values); }

		public IEnumerator<ArrayField> GetEnumerator()
		{
			if (_record.HasItems)
			{
				var arrayFields =
					from key in _record.Items[0].Keys
					let arrayField = new ArrayField(_record, key)
					where arrayField.Any()
					select arrayField;
				foreach (var arrayField in arrayFields)
				{
					yield return arrayField;
				}
			}
		}

		IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
	}

	public class TimeFieldDictionary
	{
		readonly Record _record;

		public TimeFieldDictionary(Record record) { _record = record; }

		public DateTime this[string key]
		{
			get
			{
				var value = _record[key];

				DateTime dt;
				if (DateTime.TryParse(value, out dt))
				{
					return dt;
				}

				return Utility.P4ToSystem(int.Parse(value));
			}
			set { _record[key] = Utility.SystemToP4(value).ToString(); }
		}
	}

	// this complication comes from p4's two different types of data mixed into one protocol:
	//
	//   1. filelog style output, where we have a hierarcy of records
	//   2. form style output, where we have array-fields like "View" that need collapsing
	//
	// so the ArrayField exists to translate between the two automatically.

	public class ArrayField : IList<string>
	{
		readonly IList<Record> _items;
		readonly string _key;

		public ArrayField(Record record, string key)
		{
			if (key == null) { throw new ArgumentNullException("key"); }

			_items = record.Items; // this call will auto-create the list if needed
			_key = key;
		}

		internal static string[] SplitField(string fieldValue) { return fieldValue.Replace("\r", "").TrimEnd().Split('\n'); }

		public IEnumerable<string> ToStrings(int indent)
		{
			var indentText = new string(' ', indent * 4);
			return
				from val in this
				from line in SplitField(val)
				select indentText + line;
		}

		public IEnumerable<string> ToStrings() { return ToStrings(0); }

		public override string ToString()
		{
			var sb = new StringBuilder();
			foreach (var str in ToStrings())
			{
				sb.AppendLine(str);
			}
			return sb.ToString();
		}

		public string Name { get { return _key; } }

		public void Set(IEnumerable<object> items)
		{
			var index = 0;
			foreach (var item in items)
			{
				if (_items.Count <= index)
				{
					_items.Add(new Record());
				}
				_items[index][_key] = item.ToString();
				++index;
			}

			for (; index < _items.Count; ++index)
			{
				if (!_items[index].Remove(_key))
				{
					break;
				}
			}

			CompactEnd();
		}

		public void Set(params object[] items) { Set((IEnumerable<object>)items); }

		public int IndexOf(string item)
		{
			if (item == null) { throw new ArgumentNullException("item"); }

			var index = 0;
			foreach (var val in this)
			{
				if (val == item) { return index; }
				++index;
			}
			return -1;
		}

		public void Insert(int index, string item)
		{
			InsertRange(index, WrapEnumerable(item), 1);
		}

		public void InsertRange(int index, IEnumerable<object> items)
		{
			var collection = items as ICollection<object> ?? new List<object>(items);
			InsertRange(index, collection.Select(v => v.ToString()), collection.Count);
		}

		void InsertRange(int index, IEnumerable<string> items, int itemsCount)
		{
			var oldCount = Count;
			if (index < 0 || index > oldCount) { throw new IndexOutOfRangeException(); }

			if (itemsCount > 0)
			{
				while (_items.Count < (oldCount + itemsCount))
				{
					_items.Add(new Record());
				}

				for (var i = _items.Count - 1; i >= index + itemsCount; --i)
				{
					_items[i][_key] = _items[i - itemsCount][_key];
				}

				var idst = index;
				foreach (var item in items)
				{
					_items[idst++][_key] = item;
				}
			}
		}

		public void RemoveAt(int index)
		{
			RemoveRange(index, 1);
		}

		public void RemoveRange(int index, int removeCount)
		{
			var oldCount = Count;
			if (index < 0 || removeCount < 0 || (index + removeCount) > oldCount) { throw new IndexOutOfRangeException(); }

			if (removeCount > 0)
			{
				for (var i = index + removeCount; i < oldCount; ++i)
				{
					_items[i - removeCount][_key] = _items[i][_key];
				}
				
				for (var i = oldCount - removeCount; i < oldCount; ++i)
				{
					_items[i].Remove(_key);
				}

				CompactEnd();
			}
		}

		public string this[int index]
		{
			get { return _items[index][_key]; }
			set { _items[index][_key] = value; }
		}

		public void Add(string item)
		{
			if (item == null) { throw new ArgumentNullException("item"); }

			var oldCount = Count;
			if (_items.Count < (oldCount + 1))
			{
				_items.Add(new Record());
			}
			_items[oldCount][_key] = item;
		}

		public void AddRange(IEnumerable<object> items)
		{
			if (items == null) { throw new ArgumentNullException("items"); }

			foreach (var item in items)
			{
				Add(item.ToString());
			}
		}

		public void Clear() { Set(Enumerable.Empty<string>()); }
		public bool Contains(string item) { return IndexOf(item) >= 0; }

		public void CopyTo(string[] array, int arrayIndex)
		{
			foreach (var val in this)
			{
				array[arrayIndex++] = val;
			}
		}

		public int Count { get { return _items.Count(r => r.ContainsKey(_key)); } }

		public bool IsReadOnly { get { return false; } }

		public bool Remove(string item)
		{
			var index = IndexOf(item);
			if (index >= 0)
			{
				RemoveAt(index);
				return true;
			}
			return false;
		}

		public IEnumerator<string> GetEnumerator()
		{
			foreach (var record in _items)
			{
				string val;
				if (!record.TryGetValue(_key, out val)) { break; }
				yield return val;
			}
		}

		IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

		void CompactEnd()
		{
			for (var i = _items.Count - 1; i >= 0; --i)
			{
				if (_items[i].Count == 0)
				{
					_items.RemoveAt(i);
				}
			}
		}

		IEnumerable<T> WrapEnumerable<T>(T item) { yield return item; }
	}

	public static class Utility
	{
		static readonly DateTime _p4Epoch = new DateTime(1970, 1, 1);

		public static DateTime P4ToSystem(int p4Date)
		{
			var utc = _p4Epoch.AddSeconds(p4Date);
			return TimeZone.CurrentTimeZone.ToLocalTime(utc);
		}

		public static DateTime P4ToSystem(string p4Date)
		{
			return p4Date != null ? P4ToSystem(int.Parse(p4Date)) : new DateTime();
		}

		public static int SystemToP4(DateTime date)
		{
			var utc = TimeZone.CurrentTimeZone.ToUniversalTime(date);
			var ts = utc.Subtract(_p4Epoch);
			return (int)ts.TotalSeconds;
		}

		// from .net 4
		public static bool IsNullOrWhiteSpace(string value)
		{
			return value == null || value.All(char.IsWhiteSpace);
		}

		public static Regex P4ToRegex(IEnumerable<object> patterns)
		{
			var rxtext = new StringBuilder();
			var first = true;

			foreach (var line in
				from p in patterns.Select(v => v.ToString())
				where !IsNullOrWhiteSpace(p)
				select p.Trim())
			{
				if (line.StartsWith("-"))
				{
					throw new ArgumentException("Patterns cannot contain '-' exclusions");
				}

				if (Regex.IsMatch(line, @"//.*//"))
				{
					throw new ArgumentException("Pattern contains more than one '//' - accidental joining of two patterns into a single string?");
				}

				if (!first)
				{
					rxtext.Append('|');
				}
				first = false;

				rxtext.Append('^');

				for (var i = 0; i < line.Length;)
				{
					switch (line[i])
					{
						case '.':
							if (((line.Length - i) >= 3) && (line[i + 1] == '.') && (line[i + 2] == '.'))
							{
								rxtext.Append(".*");
								i += 3;
							}
							else
							{
								rxtext.Append("\\.");
								++i;
							}
							break;
						case '*':
							rxtext.Append("[^/]*");
							++i;
							break;
						case '?':
							rxtext.Append('.');
							++i;
							break;
						default:
							rxtext.Append(Regex.Escape(line.Substring(i, 1)));
							++i;
							break;
					}
				}

				rxtext.Append('$');
			}

			return new Regex(rxtext.ToString(), RegexOptions.IgnoreCase);
		}

		public static Regex P4ToRegex(string pattern)
		{
			return P4ToRegex(new[] { pattern });
		}
	}

	public class CommandArgs
	{
		public CommandArgs(IEnumerable<object> args)
		{
			PreArgs = new List<string>();
			PostArgs = new List<string>();
			var currentArgs = PreArgs;

			using (var iarg = args.Select(v => v.ToString()).GetEnumerator())
				while (iarg.MoveNext())
				{
					var arg = iarg.Current;
					if (arg == null) { continue; }

					if (currentArgs == PreArgs)
					{
						// hyphen means it's a pre arg
						if (arg.StartsWith("-"))
						{
							currentArgs.Add(arg);

							// these options each have one arg, so grab the arg too, it's not a command
							if (Regex.IsMatch(arg, @"^-[cCdHLpPQuxz]$"))
							{
								if (iarg.MoveNext())
								{
									currentArgs.Add(iarg.Current);
								}
							}
						}
						else
						{
							Command = arg;
							currentArgs = PostArgs;
						}
					}
					else
					{
						currentArgs.Add(arg);
					}
				}
		}

		public CommandArgs(string cmdLine)
			: this(cmdLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) { }

		public static CommandArgs Parse(params object[] args)
		{
			return new CommandArgs(GetArgs(args));
		}

		static IEnumerable<object> GetArgs(IEnumerable<object> args)
		{
			foreach (var arg in args)
			{
				var objects = arg as IEnumerable<object>; // posh may nest, and that's cool..
				if (objects != null)
				{
					foreach (var o in GetArgs(objects))
					{
						yield return o;
					}
				}
				else if (arg != null)
				{
					yield return arg.ToString();
				}
			}
		}

		public IList<string> PreArgs { get; private set; }
		public string Command { get; private set; }
		public IList<string> PostArgs { get; private set; }
		public IEnumerable<string> AllArgs { get { return PreArgs.Concat(new[] { Command }).Concat(PostArgs); } }

		public override string ToString()
		{
			return string.Join(" ", AllArgs.ToArray());
		}
	}

	[DebuggerDisplay("{ShortString}")]
	public class Record : Dictionary<string, string>, IEquatable<Record>, ICloneable
	{
		static readonly Regex _nameRx = new Regex(@"^(\w+?)(\d+(?:,\d+)*)$");
		static bool _cancelOnCtrlC;

		List<Record> _items;

		public Record()
			: base(StringComparer.OrdinalIgnoreCase) { }

		public Record(Record other)
			: this()
		{
			foreach (var kv in other)
			{
				Add(kv.Key, kv.Value);
			}

			if (other._items != null)
			{
				_items = new List<Record>();
				foreach (var r in other._items)
				{
					_items.Add(new Record(r));
				}
			}
		}

		// currently only works on simply formatted forms coming from p4 itself. the spec has more features, such as comments,
		// that we aren't checking for.
		public Record(string formText)
			: this()
		{
			var keyMode = true;
			string currentKey = null;
			var currentValue = new List<string>();

			using (var reader = new StringReader(formText))
			{
				for (;;)
				{
					var line = reader.ReadLine();
					if (line == null)
					{
						break;
					}

					if (keyMode)
					{
						var match = Regex.Match(line, @"^(\w+):(?:\t(\w+))?");
						if (match.Success)
						{
							if (match.Groups[2].Success)
							{
								Add(match.Groups[1].Value, match.Groups[2].Value);
							}
							else
							{
								keyMode = false;
								currentKey = match.Groups[1].Value;
							}
						}
					}
					else if (line.Length != 0 && line[0] == '\t')
					{
						currentValue.Add(line.Substring(1));
					}
					else
					{
						Add(currentKey, string.Join("\n", currentValue.ToArray()));

						keyMode = true;
						currentKey = null;
						currentValue.Clear();
					}
				}
			}

			if (!keyMode)
			{
				Add(currentKey, string.Join("\n", currentValue.ToArray()));
			}
		}

		internal Record(BinaryReader reader)
			: this()
		{
			foreach (var kv in r_hash(reader))
			{
				var k = kv.Key;
				var v = kv.Value;
				var rec = this;

				var m = _nameRx.Match(k);
				if (m.Success)
				{
					k = m.Groups[1].Value;

					foreach (var i in
						from part in m.Groups[2].Value.Split(',')
						select int.Parse(part))
					{
						while (rec.Items.Count <= i)
						{
							rec._items.Add(new Record());
						}

						rec = rec._items[i];
					}
				}

				// special: the record may contain keys without values, which p4 uses to signify a flag. set it to 'true' to make it clear.
				if (Utility.IsNullOrWhiteSpace(v))
				{
					v = "true";
				}

				rec.Add(k, v);
			}
		}

		// global options
		public static bool CancelOnCtrlC { get { return _cancelOnCtrlC; } set { _cancelOnCtrlC = value; } }

		public IDictionary<string, string> Fields { get { return this; } }
		public TimeFieldDictionary TimeFields { get { return new TimeFieldDictionary(this); } }
		public ArrayFieldCollection ArrayFields { get { return new ArrayFieldCollection(this); } }
		public IList<Record> Items { get { return _items ?? (_items = new List<Record>()); } }
		public bool HasItems { get { return _items != null && _items.Count != 0; } }
		public bool IsInfo { get { return string.Compare(this["code"], "info", true) == 0; } }
		public bool IsFailure { get { return string.Compare(this["code"], "error", true) == 0; } }
		public bool IsError { get { return ErrorSeverity >= 3; } }
		public bool IsWarning { get { return ErrorSeverity > 0 && !IsError; } }

		public int ErrorSeverity
		{
			get
			{
				if (!IsFailure) { return 0; }

				int severity;
				int.TryParse(this["severity"], out severity);
				return severity;
			}
		}

		public string[] SortedFieldKeys
		{
			get
			{
				var keys = new string[Count];
				Keys.CopyTo(keys, 0);
				Array.Sort(keys);
				return keys;
			}
		}

		/// <summary>
		/// Call this to run a P4 command.
		/// </summary>
		/// <param name="workingDir">Working dir for P4. Necessary when relying on p4.ini or using relative local paths. Optional, defaults to .NET current dir.</param>
		/// <param name="cmdLine">The command line to send to p4.exe. Make sure to quote paths with spaces. Required.</param>
		/// <param name="input">An input record to send in to a command that takes an input form, such as 'client -i'. Optional.</param>
		/// <param name="lazy">Controls whether the enumerable is lazily evaluated or not. True means minimal memory usage, immediate results and best performance, but
		/// also puts a burden on the client of needing to consume the entire thing to guarantee operations like 'sync' finish. Optional, defaults to false.</param>
		/// <returns>All results from P4, reprocessed into Record objects</returns>
		public static IEnumerable<Record> Run(string workingDir, string cmdLine, Record input, bool lazy)
		{
			var records = RunLazy(workingDir, cmdLine, input);
			if (!lazy)
			{
				records = records.ToList();
			}

			return records;
		}

		public static IEnumerable<Record> Run(string workingDir, string cmdLine, Record input)
		{ return Run(workingDir, cmdLine, input, false); }
		public static IEnumerable<Record> Run(string workingDir, string cmdLine, bool lazy)
		{ return Run(workingDir, cmdLine, null, lazy); }
		public static IEnumerable<Record> Run(string workingDir, string cmdLine)
		{ return Run(workingDir, cmdLine, null, false); }
		public static IEnumerable<Record> Run(string cmdLine, Record input, bool lazy)
		{ return Run(null, cmdLine, input, lazy); }
		public static IEnumerable<Record> Run(string cmdLine, Record input)
		{ return Run(null, cmdLine, input, false); }
		public static IEnumerable<Record> Run(string cmdLine, bool lazy)
		{ return Run(null, cmdLine, null, lazy); }
		public static IEnumerable<Record> Run(string cmdLine)
		{ return Run(null, cmdLine, null, false); }

		static IEnumerable<Record> RunLazy(string workingDir, string cmdLine, Record input)
		{
			return new Reader(workingDir, cmdLine, input).Run();
		}

		public string ShortString
		{
			get
			{
				const int maxFields = 10, maxFieldLen = 50;

				var kv = new List<string>(Keys);
				kv.Sort();
				if (kv.Count > maxFields)
				{
					kv.RemoveRange(maxFields, kv.Count - maxFields);
				}

				for (var i = 0; i < kv.Count; ++i)
				{
					var k = kv[i];
					var v = this[k];
					if (v.Length > maxFieldLen) { v = v.Substring(0, maxFieldLen - 3) + "..."; }
					kv[i] = k + "=" + v;
				}

				var fields = string.Join(", ", kv.ToArray());
				if (Count > maxFields) { fields += ", ..."; }
				if (_items != null && _items.Count > 0) { fields += " (+" + _items.Count + " items)"; }

				return fields;
			}
		}

		// this is really just to have a familiar face. for submitting a form to p4,
		// send the Record through the "input" field of Run().
		public string ToFormString()
		{
			var sb = new StringBuilder();

			foreach (var kv in this.Where(kv => !ShouldSkipField(kv.Key)))
			{
				if (kv.Value.Contains("\n"))
				{
					sb.AppendLine(kv.Key + ":");
					foreach (var line in ArrayField.SplitField(kv.Value))
					{
						sb.AppendLine("\t" + line);
					}
				}
				else
				{
					sb.AppendLine(kv.Key + ":\t" + kv.Value);
				}
				sb.AppendLine();
			}

			foreach (var arrayField in ArrayFields)
			{
				sb.AppendLine(arrayField.Name + ":");
				foreach (var entry in arrayField)
				{
					sb.AppendLine("\t" + entry);
				}
				sb.AppendLine();
			}
			return sb.ToString();
		}

		public IEnumerable<string> ToStrings(int indent)
		{
			var indentText = new string(' ', indent * 4);
			foreach (var kv in this)
			{
				var first = true;
				foreach (var line in ArrayField.SplitField(kv.Value))
				{
					if (first)
					{
						yield return indentText + kv.Key + " = " + line;
						first = false;
					}
					else
					{
						yield return indentText + new string(' ', kv.Key.Length + 3) + line;
					}
				}
			}

			if (_items != null)
			{
				var index = 0;
				foreach (var record in _items)
				{
					yield return indentText + "  [" + index++ + "]";
					foreach (var str in record.ToStrings(indent + 1))
					{
						yield return str;
					}
				}
			}
		}

		public IEnumerable<string> ToStrings() { return ToStrings(0); }

		public override string ToString()
		{
			var sb = new StringBuilder();
			foreach (var str in ToStrings())
			{
				sb.AppendLine(str);
			}
			return sb.ToString();
		}

		public bool Equals(Record other)
		{
			if (ReferenceEquals(null, other)) { return false; }
			if (ReferenceEquals(this, other)) { return true; }

			if (Count != other.Count) { return false; }

			var itemCount = _items != null ? _items.Count : 0;
			var otherItemCount = other._items != null ? other._items.Count : 0;
			if (itemCount != otherItemCount) { return false; }

			foreach (var kv in this)
			{
				string otherValue;
				if (!other.TryGetValue(kv.Key, out otherValue) || !Equals(kv.Value, otherValue)) { return false; }
			}

			for (var i = 0; i < itemCount; ++i)
			{
				// ReSharper disable PossibleNullReferenceException
				if (!_items[i].Equals(other._items[i])) { return false; }
				// ReSharper restore PossibleNullReferenceException
			}

			return true;
		}

		public override bool Equals(object obj)
		{
			if (ReferenceEquals(this, obj)) { return true; }

			var other = obj as Record;
			return !ReferenceEquals(other, null) && Equals(other);
		}

		public override int GetHashCode()
		{
			var hash = SortedFieldKeys.Aggregate(0,
				(current, k) => current ^ (Comparer.GetHashCode(k) ^ this[k].GetHashCode()));

			// ReSharper disable NonReadonlyFieldInGetHashCode
			if (_items != null)
			{
				hash = _items.Aggregate(hash, (current, r) => current ^ r.GetHashCode());
			}
			// ReSharper restore NonReadonlyFieldInGetHashCode

			return hash;
		}

		public static bool operator ==(Record left, Record right) { return Equals(left, right); }
		public static bool operator !=(Record left, Record right) { return !Equals(left, right); }

		public Record Clone() { return new Record(this); }
		object ICloneable.Clone() { return Clone(); }

		static bool ShouldSkipField(string fieldName)
		{
			// add to this list as needed
			return string.Compare(fieldName, "code", true) == 0;
		}

		void Write(BinaryWriter writer)
		{
			writer.Write(MARSHAL_MAJOR);
			writer.Write(MARSHAL_MINOR);

			// only support one level of depth for forms
			var oldCount = Count;
			if (_items != null)
			{
				foreach (var record in _items)
				{
					oldCount += record.Count;
					if (record._items != null && record._items.Count > 0)
					{
						throw new ApplicationException("Only one level of nesting is supported for forms sent to Perforce");
					}
				}
			}

			w_hash(GetHashStream(), oldCount, writer);
		}

		IEnumerable<KeyValuePair<string, string>> GetHashStream()
		{
			foreach (var kv in this.Where(kv => !ShouldSkipField(kv.Key)))
			{
				yield return kv;
			}

			if (_items != null && _items.Count > 0)
			{
				foreach (var k in _items[0].Keys)
				{
					var index = 0;
					foreach (var record in _items)
					{
						string v;
						if (!record.TryGetValue(k, out v)) { break; }

						yield return new KeyValuePair<string, string>(k + index++, v);
					}
				}
			}
		}

		#region Reader

		sealed class Reader
		{
			readonly object _p4LockObject = new object();
			readonly ManualResetEvent _cancelRequested = new ManualResetEvent(false);
			readonly List<Record> _records = new List<Record>();
			readonly CommandArgs _commandArgs;

			Process _p4;
			int _finishedCount;

#			if DEBUG_SINGLE_RECORD_STREAM
			const int _recordBufferSize = 1;
#			else
			const int _recordBufferSize = 1000;
#			endif

			public Reader(string workingDir, string cmdLine, Record input)
			{
				if (cmdLine == null) { throw new ArgumentNullException("cmdLine"); }

				// grab command in case we need to do special parsing
				_commandArgs = new CommandArgs(cmdLine);

				// fire up process
				_p4 = Process.Start(new ProcessStartInfo("p4", "-R " + cmdLine)
					{
						// note that stdin must always be redirected, otherwise we'll get a hang instead of an error if
						// someone does "client -i" without an input record.

						WorkingDirectory = workingDir,
						CreateNoWindow = true,
						UseShellExecute = false,
						RedirectStandardError = true,
						RedirectStandardOutput = true,
						RedirectStandardInput = true
					});

				if (_cancelOnCtrlC)
				{
					Console.CancelKeyPress += Console_CancelKeyPress;
				}

				// start readers

				new Thread(StderrReader) { Name = "p4nano.StderrReader" }.Start();
				new Thread(StdoutReader) { Name = "p4nano.StdoutReader" }.Start();

				// write any input requested

				lock (_p4LockObject)
				{
					if (input != null)
					{
						input.Write(new BinaryWriter(_p4.StandardInput.BaseStream));
					}

					_p4.StandardInput.Close();
				}
			}

			public IEnumerable<Record> Run()
			{
				try
				{
					for (;;)
					{
						var recordSet = GetNextRecordSet();
						if (recordSet == null)
						{
							// null return means end of stream or cancel
							break;
						}

						foreach (var record in recordSet)
						{
							yield return record;
						}
					}
				}
				finally
				{
					// this is called on enumerator disposal or if an exception occurs during iteration. best
					// place to kill the process if it wasn't done already.
					Cancel();
				}
			}

			void Cancel()
			{
				// ideally this would send a ctrl-c but can't figure that out. killing the process will work
				// but will prevent p4.exe from cleaning up after itself, so may get temp files laying around.
				// http://stackoverflow.com/questions/297615/stuck-on-generateconsolectrlevent-in-c-with-console-apps

				lock (_p4LockObject)
				{
					if (_p4 != null && !_p4.HasExited)
					{
						try { _p4.Kill(); }
						catch { }
					}
				}

				_cancelRequested.Set();
			}

			IEnumerable<Record> GetNextRecordSet()
			{
				for (;;)
				{
					lock (_records)
					{
						if (_records.Count > 0)
						{
							var records = _records.ToArray();
							_records.Clear();
							return records;
						}

						if (_finishedCount == 2)
						{
							return null;
						}
					}

					// give time for records to be retrieved by p4.exe or read by worker threads
					Thread.Sleep(1);
				}
			}

			void StdoutReader() { Exec(StdoutReaderExec); }

			void StdoutReaderExec()
			{
				var stdout = _p4.StandardOutput.BaseStream;

				// special processing first
				if (_commandArgs.Command == "set")
				{
					var record = new Record();
					record["help"] = "Check the Items array for records"; // too easy to test "p4n set" and get (apparently) nothing back and forget to route through tostring to see the items

					var reader = new StreamReader(stdout);
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						var m = Regex.Match(line, @"(.*)=(.*?)(?:\s+\(([^)]*)\))?$");
						var entry = new Record();
						entry["var"] = m.Groups[1].Value.Trim();
						entry["value"] = m.Groups[2].Value.Trim();
						if (m.Groups[3].Success)
						{
							entry["where"] = m.Groups[3].Value.Trim();
						}
						record.Items.Add(entry);
					}

					lock (_records)
					{
						_records.Add(record);
					}

					return;
				}

				for (;;)
				{
					var recordMajor = stdout.ReadByte();
					var recordMinor = stdout.ReadByte();

					// -1 means end of stream (process exit)
					if (recordMajor < 0)
					{
						return;
					}

					// only check major version, that should be the only time a format change happens we might care about
					if (recordMajor != MARSHAL_MAJOR)
					{
						throw new ApplicationException(
							string.Format("Unsupported version {0}.{1}", recordMajor, recordMinor));
					}

					// read the next record
					var record = new Record(new BinaryReader(stdout));

					// special processing
					if (_commandArgs.Command == "change" && _commandArgs.PostArgs.Contains("-i") && record.IsInfo && !record.ContainsKey("change"))
					{
						var g = Regex.Match(record["data"], @"Change (\d+) created").Groups[1];
						if (g.Success)
						{
							record["change"] = g.Value;
						}
					}

					// loop until we have room to insert the next record
					for (;;)
					{
						lock (_records)
						{
							if (_records.Count < _recordBufferSize)
							{
								_records.Add(record);
								break;
							}
						}

						// give time for records to be pulled by enumerator
						if (_cancelRequested.WaitOne(1))
						{
							return;
						}
					}
				}
			}

			void StderrReader() { Exec(StderrReaderExec); }

			void StderrReaderExec()
			{
				var sb = new StringBuilder();
				for (;;)
				{
					var c = _p4.StandardError.Read();

					// -1 means end of stream (process exit)
					if (c < 0)
					{
						if (sb.Length > 0)
						{
							var record = new Record();
							record["code"] = "error";
							record["data"] = sb.ToString();
							record["severity"] = "3";

							lock (_records)
							{
								_records.Add(record);
							}
						}

						return;
					}

					sb.Append((char)c);
				}
			}

			delegate void Action();

			void Exec(Action reader)
			{
				try
				{
					reader();
				}
				finally
				{
					if (Interlocked.Increment(ref _finishedCount) == 2)
					{
						Console.CancelKeyPress -= Console_CancelKeyPress;

						lock (_p4LockObject)
						{
							// give it a little bit to finish cleaning up if exiting normally
							if (!_p4.WaitForExit(100))
							{
								try { _p4.Kill(); }
								catch { }
							}

							// release system handle, don't leave it for finalizer
							_p4.Dispose();
							_p4 = null;
						}
					}
				}
			}

			void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
			{
				Cancel();
			}
		}

		#endregion

		#region Ruby Parser

		// adapted from http://ruby-doc.org/doxygen/1.8.4/marshal_8c-source.html

		// ReSharper disable InconsistentNaming
		const byte MARSHAL_MAJOR = 4;
		const byte MARSHAL_MINOR = 8;
		const byte TYPE_FIXNUM = (byte)'i';
		const byte TYPE_STRING = (byte)'"';
		const byte TYPE_HASH = (byte)'{';
		// ReSharper restore InconsistentNaming

		static int r_long(BinaryReader reader)
		{
			int x;
			int c = (char)reader.ReadByte();
			int i;

			if (c == 0) { return 0; }
			if (c > 0)
			{
				if (4 < c && c < 128) { return c - 5; }
				if (c > sizeof(int)) { throw new ApplicationException("Int too big: " + c); }
				x = 0;
				for (i = 0; i < c; i++)
				{
					x |= reader.ReadByte() << (8 * i);
				}
			}
			else
			{
				if (-129 < c && c < -4)
				{
					return c + 5;
				}
				c = -c;
				if (c > sizeof(int)) { throw new ApplicationException("Int too big: " + c); }
				x = -1;
				for (i = 0; i < c; i++)
				{
					x &= ~(0xff << (8 * i));
					x |= reader.ReadByte() << (8 * i);
				}
			}
			return x;
		}

		static void w_long(int x, BinaryWriter writer)
		{
			if (x == 0)
			{
				writer.Write((byte)0);
				return;
			}
			if (0 < x && x < 123)
			{
				writer.Write((byte)(x + 5));
				return;
			}
			if (-124 < x && x < 0)
			{
				writer.Write((byte)((x - 5) & 0xff));
				return;
			}

			var buf = new byte[sizeof(int) + 1];
			byte i;
			for (i = 1; i < sizeof(int) + 1; i++)
			{
				buf[i] = (byte)(x & 0xff);
				x = x >> 8;
				if (x == 0)
				{
					buf[0] = i;
					break;
				}
				if (x == -1)
				{
					buf[0] = (byte)-i;
					break;
				}
			}
			writer.Write(buf, 0, i + 1);
		}

		static string r_object_as_string(BinaryReader reader)
		{
			var c = reader.ReadByte();
			switch (c)
			{
				case TYPE_FIXNUM:
					return r_long(reader).ToString();

				case TYPE_STRING:
					return Encoding.ASCII.GetString(reader.ReadBytes(r_long(reader)));

				default: throw new ApplicationException(string.Format("Unrecognized type: {0} ({1})", c, (int)c));
			}
		}

		// ReSharper disable UnusedMember.Local
		static void w_object(int i, BinaryWriter writer)
		{
			writer.Write(TYPE_FIXNUM);
			w_long(i, writer);
		}
		// ReSharper restore UnusedMember.Local

		static void w_object(string s, BinaryWriter writer)
		{
			writer.Write(TYPE_STRING);
			var b = Encoding.ASCII.GetBytes(s);
			w_long(b.Length, writer);
			writer.Write(b);
		}

		static IEnumerable<KeyValuePair<string, string>> r_hash(BinaryReader reader)
		{
			// 'p4 -R' does Ruby dictionaries very simply - a set of records, each containing a hashtable where
			// each entry is a string key and a string or int value. we use Ruby output instead of -Ztag because
			// the "..." output can have ambiguity when a text description field is involved.

			// should contain a single hash
			var hashType = reader.ReadByte();
			if (hashType != TYPE_HASH)
			{
				throw new ApplicationException(string.Format("Unrecognized type: {0} ({1})", (char)hashType, (int)hashType));
			}

			// reprocess into the format we want as we go
			for (var hashCount = r_long(reader); hashCount > 0; --hashCount)
			{
				var k = r_object_as_string(reader);
				var v = r_object_as_string(reader);
				yield return new KeyValuePair<string, string>(k, v);
			}
		}

		static void w_hash(IEnumerable<KeyValuePair<string, string>> kvs, int count, BinaryWriter writer)
		{
			writer.Write(TYPE_HASH);
			w_long(count, writer);
			foreach (var kv in kvs)
			{
				w_object(kv.Key, writer);
				w_object(kv.Value, writer);
			}
		}

		#endregion
	}
}
