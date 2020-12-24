using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector.Logging;
using MySqlConnector.Protocol.Serialization;

namespace MySqlConnector.Core
{
	internal static class BackgroundConnectionResetHelper
	{
		public static void AddSession(ServerSession session)
		{
			// TODO: save the MySqlConnection object so this session isn't considered leaked

			var resetTask = session.TryResetConnectionAsync(session.Pool!.ConnectionSettings, IOBehavior.Asynchronous, default);
			lock (s_lock)
				s_sessions.Add(new SessionResetTask(session, resetTask));

			if (Log.IsDebugEnabled())
				Log.Debug("Started Session{0} reset in background; waiting SessionCount: {1}.", session.Id, s_sessions.Count);

			// release only if it is likely to succeed
			if (s_semaphore.CurrentCount == 0)
			{
				Log.Debug("Releasing semaphore.");
				try
				{
					s_semaphore.Release();
				}
				catch (SemaphoreFullException)
				{
					// ignore
				}
			}
		}

		public static void Start()
		{
			Log.Info("Starting BackgroundConnectionResetHelper worker.");
			lock (s_lock)
			{
				if (s_workerTask is null)
					s_workerTask = Task.Run(async () => await ReturnSessionsAsync());
			}
		}

		public static void Stop()
		{
			Log.Info("Stopping BackgroundConnectionResetHelper worker.");
			s_cancellationTokenSource.Cancel();
			Task? workerTask;
			lock (s_lock)
				workerTask = s_workerTask;

			if (workerTask is not null)
			{
				try
				{
					workerTask.GetAwaiter().GetResult();
				}
				catch (OperationCanceledException)
				{
				}
			}
			Log.Info("Stopped BackgroundConnectionResetHelper worker.");
		}

		public static async Task ReturnSessionsAsync()
		{
			Log.Info("Started BackgroundConnectionResetHelper worker.");

			List<Task<bool>> localTasks = new();
			List<ServerSession> localSessions = new();

			// keep running until stopped
			while (!s_cancellationTokenSource.IsCancellationRequested)
			{
				try
				{
					// block until AddSession releases the semaphore
					Log.Info("Waiting for semaphore.");
					await s_semaphore.WaitAsync(s_cancellationTokenSource.Token).ConfigureAwait(false);

					// process all sessions that have started being returned
					while (true)
					{
						lock (s_lock)
						{
							if (s_sessions.Count == 0)
							{
								if (localTasks.Count == 0)
									break;
							}
							else
							{
								foreach (var data in s_sessions)
								{
									localSessions.Add(data.Session);
									localTasks.Add(data.ResetTask);
								}
								s_sessions.Clear();
							}
						}

						if (Log.IsDebugEnabled())
							Log.Debug("Found SessionCount {0} session(s) to return.", localSessions.Count);

						while (localTasks.Count != 0)
						{
							var completedTask = await Task.WhenAny(localTasks).ConfigureAwait(false);
							var index = localTasks.IndexOf(completedTask);
							var session = localSessions[index];
							await session.Pool!.ReturnAsync(IOBehavior.Asynchronous, session).ConfigureAwait(false);
							localSessions.RemoveAt(index);
							localTasks.RemoveAt(index);
						}
					}
				}
				catch (Exception ex) when (!(ex is OperationCanceledException oce && oce.CancellationToken == s_cancellationTokenSource.Token))
				{
					Log.Error("Unhandled exception: {0}", ex);
				}
			}
		}

		internal struct SessionResetTask
		{
			public SessionResetTask(ServerSession session, Task<bool> resetTask)
			{
				Session = session;
				ResetTask = resetTask;
			}

			public ServerSession Session { get; }
			public Task<bool> ResetTask { get; }
		}

		static readonly IMySqlConnectorLogger Log = MySqlConnectorLogManager.CreateLogger(nameof(BackgroundConnectionResetHelper));
		static readonly object s_lock = new();
		static readonly SemaphoreSlim s_semaphore = new(1, 1);
		static readonly CancellationTokenSource s_cancellationTokenSource = new();
		static List<SessionResetTask> s_sessions = new();
		static Task? s_workerTask;
	}
}
