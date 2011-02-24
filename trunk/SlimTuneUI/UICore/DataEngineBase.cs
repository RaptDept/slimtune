﻿/*
* Copyright (c) 2007-2010 SlimDX Group
* 
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
* 
* The above copyright notice and this permission notice shall be included in
* all copies or substantial portions of the Software.
* 
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
* THE SOFTWARE.
*/
using System;
using System.Data;
using System.Collections.Generic;
using System.Text;

using NHibernate;
using NHibernate.Cfg;
using NHibernate.Tool.hbm2ddl;
using FluentNHibernate.Cfg;
using FluentNHibernate.Cfg.Db;

namespace UICore
{
	public struct CallGraph<T>
	{
		//this is: ThreadId, ParentId, ChildId, HitCount
		public SortedList<int, SortedDictionary<int, SortedList<int, T>>> Graph;

		public static CallGraph<T> Create()
		{
			CallGraph<T> cg = new CallGraph<T>();
			cg.Graph = new SortedList<int, SortedDictionary<int, SortedList<int, T>>>(8);
			return cg;
		}
	}

	public struct AllocData
	{
		public long Size;
		public int Count;

		public void Add(long size)
		{
			++Count;
			Size += size;
		}
	}

	public abstract class DataEngineBase : IDataEngine
	{
		protected const string kCallsSchema = "(ThreadId INT NOT NULL, ParentId INT NOT NULL, ChildId INT NOT NULL, HitCount INT NOT NULL)";
		protected const string kSamplesSchema = "(ThreadId INT NOT NULL, FunctionId INT NOT NULL, HitCount INT NOT NULL)";

		private const int kFunctionCacheSize = 64;
		private const int kClassCacheSize = 32;

		private List<FunctionInfo> m_functionCache = new List<FunctionInfo>(kFunctionCacheSize);
		private List<ClassInfo> m_classCache = new List<ClassInfo>(kClassCacheSize);

		//Everything is stored sorted so that we can sprint through the database quickly
		protected CallGraph<double> m_calls = CallGraph<double>.Create();
		//this is: FunctionId, ThreadId, HitCount
		protected SortedDictionary<int, SortedList<int, double>> m_samples = new SortedDictionary<int, SortedList<int, double>>();

		//this is: ClassId, FunctionId, AllocData
		protected SortedDictionary<int, SortedDictionary<int, AllocData>> m_allocs = new SortedDictionary<int, SortedDictionary<int, AllocData>>();

		private DateTime m_lastFlush = DateTime.UtcNow;
		//we use this so we don't have to check DateTime.Now on every single sample
		protected int m_cachedSamples;

		private ISessionFactory m_sessionFactory;
		private IPersistenceConfigurer m_configurer;
		private Configuration m_config;
		private IStatelessSession m_statelessSession;
		private Dictionary<int, ISessionFactory> m_snapshotFactories = new Dictionary<int, ISessionFactory>(8);

		protected object m_lock = new object();

		public string Name
		{
			get;
			private set;
		}

		public abstract string Engine
		{
			get;
		}

		public abstract string Extension
		{
			get;
		}

		public abstract System.Data.IDbConnection Connection
		{
			get;
		}

		public virtual bool InMemory
		{
			get { return false; }
		}

		private bool ShouldFlush
		{
			get
			{
				var span = DateTime.UtcNow - m_lastFlush;
				if(span.TotalSeconds > 5.0)
					return true;
				return false;
			}
		}

		protected virtual void PreCreateSchema() { }
		protected abstract void PrepareCommands();
		protected abstract void DoFlush();
		public abstract void Save(string file);

		public IDataReader SqlQuery(string query)
		{
			using(var cmd = Connection.CreateCommand())
			{
				cmd.CommandText = query;
				return cmd.ExecuteReader();
			}
		}

		public object SqlScalar(string query)
		{
			using(var cmd = Connection.CreateCommand())
			{
				cmd.CommandText = query;
				return cmd.ExecuteScalar();
			}
		}

		public void SqlCommand(string query)
		{
			using(var cmd = Connection.CreateCommand())
			{
				cmd.CommandText = query;
				cmd.ExecuteNonQuery();
			}
		}

		public DataEngineBase(string name)
		{
			Name = name;
		}

		protected void FinishConstruct(bool createNew, IPersistenceConfigurer configurer)
		{
			m_configurer = configurer;
			var fluentConfig = Fluently.Configure().Database(configurer)
				.Mappings(m => m.FluentMappings.AddFromAssemblyOf<DataEngineBase>());
			m_config = fluentConfig.BuildConfiguration();
			m_sessionFactory = m_config.BuildSessionFactory();
			m_statelessSession = OpenStatelessSession();

			if(createNew)
			{
				PreCreateSchema();
				var export = new SchemaExport(m_config);
				export.Execute(true, true, false, Connection, Console.Out);

				WriteCoreProperties();
				//create an entry for the first snapshot in the db
				var firstSnapshot = m_statelessSession.CreateSQLQuery("INSERT INTO Snapshots (Id, Name, DateTime) VALUES (:id, :name, :datetime)")
					.SetInt32("id", 0)
					.SetString("name", "Current")
					.SetInt64("datetime", long.MaxValue);
				firstSnapshot.ExecuteUpdate();
			}
			else
			{
				using(var session = OpenSession())
				using(var tx = session.BeginTransaction())
				{
					var applicationProp = session.Get<Property>("Application");
					if(applicationProp == null || applicationProp.Value != System.Windows.Forms.Application.ProductName)
					{
						throw new System.IO.InvalidDataException("Wrong or missing application name.");
					}

					var versionProp = session.Get<Property>("FileVersion");
					if(versionProp == null || int.Parse(versionProp.Value) != 3)
					{
						throw new System.IO.InvalidDataException("Wrong file version.");
					}

					tx.Commit();
				}
			}

			PrepareCommands();
		}

		public virtual NHibernate.ISession OpenSession()
		{
			return m_sessionFactory.OpenSession(Connection);
		}

		public virtual NHibernate.ISession OpenSession(int snapshot)
		{
			var session = m_sessionFactory.OpenSession(Connection);
			session.EnableFilter("Snapshot").SetParameter("snapshot", snapshot);
			return session;
		}

		public virtual NHibernate.IStatelessSession OpenStatelessSession()
		{
			return m_sessionFactory.OpenStatelessSession(Connection);
		}

		public void Flush()
		{
			lock(m_lock)
			{
				using(var tx = m_statelessSession.BeginTransaction())
				{
					//flush functions
					foreach(var f in m_functionCache)
					{
						try
						{
							m_statelessSession.Insert(f);
						}
						catch(NHibernate.Exceptions.GenericADOException)
						{
							//this can happen with duplicates, which should not be possible with the caching in ProfilerClient
							//that may change, though, and we want to be robust to this particular failure
						}
					}
					m_functionCache.Clear();

					//flush classes
					foreach(var c in m_classCache)
					{
						try
						{
							m_statelessSession.Insert(c);
						}
						catch(NHibernate.Exceptions.GenericADOException)
						{
							//see above
						}
					}
					m_classCache.Clear();

					tx.Commit();
				}

				DoFlush();
				m_lastFlush = DateTime.UtcNow;
			}
		}

		public void ParseSample(Messages.Sample sample)
		{
			lock(m_lock)
			{
				//Update calls
				SortedDictionary<int, SortedList<int, double>> perThread;
				bool foundThread = m_calls.Graph.TryGetValue(sample.ThreadId, out perThread);
				if(!foundThread)
				{
					perThread = new SortedDictionary<int, SortedList<int, double>>();
					m_calls.Graph.Add(sample.ThreadId, perThread);
				}

				Increment(sample.Functions[0], 0, perThread, sample.Time);
				for(int f = 1; f < sample.Functions.Count; ++f)
				{
					Increment(sample.Functions[f], sample.Functions[f - 1], perThread, sample.Time);
				}
				Increment(0, sample.Functions[sample.Functions.Count - 1], perThread, sample.Time);

				//Update overall samples count
				for(int s = 0; s < sample.Functions.Count; ++s)
				{
					int functionId = sample.Functions[s];
					bool first = true;

					//scan backwards to see if this was elsewhere in the stack and therefore already counted
					for(int r = s - 1; r >= 0; --r)
					{
						if(functionId == sample.Functions[r])
						{
							//yep, it was
							first = false;
							break;
						}
					}

					if(first)
					{
						//add the function if we don't have it yet
						if(!m_samples.ContainsKey(functionId))
							m_samples.Add(functionId, new SortedList<int, double>());

						//add this thread if we don't have it, else just increment
						if(!m_samples[functionId].ContainsKey(sample.ThreadId))
							m_samples[functionId].Add(sample.ThreadId, sample.Time);
						else
							m_samples[sample.Functions[s]][sample.ThreadId] += sample.Time;
					}
				}

				++m_cachedSamples;
				if(m_cachedSamples > 2000 || ShouldFlush)
				{
					Flush();
				}
			}
		}

		public void Snapshot(string name)
		{
			lock(m_lock)
			{
				Flush();

				using(var session = OpenSession())
				using(var tx = session.BeginTransaction(IsolationLevel.Serializable))
				{
					Snapshot snapshot = new Snapshot();
					snapshot.Name = name;
					snapshot.DateTime = DateTime.Now.ToFileTime();
					session.Save(snapshot);

					string sampleQuery = string.Format("insert into Sample (ThreadId, FunctionId, Time, SnapshotId) select s.ThreadId, s.FunctionId, s.Time, {0} from Sample s where SnapshotId = 0", snapshot.Id);
					session.CreateQuery(sampleQuery).ExecuteUpdate();
					string callQuery = string.Format("insert into Call (ThreadId, ParentId, ChildId, Time, SnapshotId) select ThreadId, ParentId, ChildId, Time, {0} from Call where SnapshotId = 0", snapshot.Id);
					session.CreateQuery(callQuery).ExecuteUpdate();

					tx.Commit();
				}
			}
		}

		public void ClearData()
		{
			lock(m_lock)
			{
				foreach(KeyValuePair<int, SortedDictionary<int, SortedList<int, double>>> threadKvp in m_calls.Graph)
				{
					int threadId = threadKvp.Key;
					foreach(KeyValuePair<int, SortedList<int, double>> callerKvp in threadKvp.Value)
					{
						callerKvp.Value.Clear();
					}
				}
				m_lastFlush = DateTime.UtcNow;
				m_samples.Clear();
				m_cachedSamples = 0;

				using(var session = OpenSession())
				using(var tx = session.BeginTransaction(IsolationLevel.Serializable))
				{
					session.CreateQuery("delete from Call where SnapshotId = 0").ExecuteUpdate();
					session.CreateQuery("delete from Sample where SnapshotId = 0").ExecuteUpdate();
					tx.Commit();
				}
			}
		}

		public virtual void WriteProperty(string name, string value)
		{
			using(var session = OpenSession())
			using(var tx = session.BeginTransaction())
			{
				WriteProperty(session, name, value);
				tx.Commit();
			}
		}

		public virtual void WriteProperty(ISession session, string name, string value)
		{
			var prop = new Property() { Name = name, Value = value };
			session.SaveOrUpdateCopy(prop);
		}

		public virtual string GetProperty(string name)
		{
			using(var session = OpenSession())
			{
				var prop = session.Get<Property>(name);
				if(prop == null)
					return null;

				return prop.Value;
			}
		}

		protected void WriteCoreProperties()
		{
			using(var session = OpenSession(0))
			using(var tx = session.BeginTransaction(IsolationLevel.Serializable))
			{
				WriteProperty(session, "Application", System.Windows.Forms.Application.ProductName);
				WriteProperty(session, "Version", System.Windows.Forms.Application.ProductVersion);
				WriteProperty(session, "FileVersion", "3");
				WriteProperty(session, "FileName", Name);

				tx.Commit();
			}
		}

		public virtual void FunctionTiming(int functionId, long time)
		{
			throw new NotImplementedException();
		}

		protected static void Increment(int key1, int key2, SortedDictionary<int, SortedList<int, double>> container, double time)
		{
			SortedList<int, double> key1Table;
			bool foundKey1Table = container.TryGetValue(key1, out key1Table);
			if(!foundKey1Table)
			{
				key1Table = new SortedList<int, double>();
				container.Add(key1, key1Table);
			}

			if(!key1Table.ContainsKey(key2))
			{
				key1Table.Add(key2, time);
			}
			else
			{
				key1Table[key2] += time;
			}
		}

		public virtual void MapFunction(FunctionInfo funcInfo)
		{
			m_functionCache.Add(funcInfo);
			if(m_functionCache.Count >= kFunctionCacheSize || ShouldFlush)
				Flush();
		}

		public virtual void MapClass(ClassInfo classInfo)
		{
			m_classCache.Add(classInfo);
			if(m_classCache.Count >= kClassCacheSize || ShouldFlush)
				Flush();
		}

		public virtual void UpdateThread(int threadId, bool alive, string name)
		{
			var ti = new ThreadInfo() { Id = threadId, IsAlive = alive, Name = name };
			using(var session = OpenSession(0))
			using(var tx = session.BeginTransaction())
			{
				session.SaveOrUpdateCopy(ti, ti.Id);
				tx.Commit();
			}
		}

		public virtual void MapCounter(Counter counter)
		{
			using(var session = OpenSession(0))
			using(var tx = session.BeginTransaction())
			{
				session.SaveOrUpdateCopy(counter);
				tx.Commit();
			}
		}

		public virtual void PerfCounter(int counterId, long time, double value)
		{
			using(var tx = m_statelessSession.BeginTransaction())
			{
				var cv = new CounterValue()
				{
					CounterId = counterId,
					Time = time,
					Value = value
				};
				m_statelessSession.Insert(cv);
				tx.Commit();
			}
		}

		public virtual void ObjectAllocated(int classId, long size, int functionId, long time)
		{
			SortedDictionary<int, AllocData> byFunc;
			if(m_allocs.ContainsKey(classId))
			{
				byFunc = m_allocs[classId];
			}
			else
			{
				byFunc = new SortedDictionary<int, AllocData>();
				m_allocs.Add(classId, byFunc);
			}

			if(byFunc.ContainsKey(functionId))
				byFunc[functionId].Add(size);
			else
				byFunc.Add(functionId, new AllocData() { Count = 1, Size = size });
		}

		public virtual void GarbageCollection(int generation, int function, long time)
		{
			using(var tx = m_statelessSession.BeginTransaction())
			{
				var gc = new GarbageCollection()
				{
					Generation = generation,
					FunctionId = function,
					Time = time
				};
				m_statelessSession.Insert(gc);
				tx.Commit();
			}

			Flush();
		}

		#region IDisposable Members

		public virtual void Dispose()
		{
			if(m_statelessSession != null)
				m_statelessSession.Dispose();
			if(m_sessionFactory != null)
				m_sessionFactory.Dispose();
		}

		#endregion
	}
}
