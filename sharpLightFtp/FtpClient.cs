﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using sharpLightFtp.EventArgs;
using sharpLightFtp.Extensions;

namespace sharpLightFtp
{
	public sealed class FtpClient : IDisposable
	{
		private readonly Lazy<FtpFeatures> _ftpFeatures;
		private readonly object _lockControlComplexSocket = new object();
		private ComplexSocket _controlComplexSocket;

		public FtpClient(string username,
		                 string password,
		                 string server,
		                 int port)
			: this(username,
			       password,
			       server)
		{
			this.Port = port;
		}

		public FtpClient(string username,
		                 string password,
		                 string server)
			: this(username,
			       password)
		{
			this.Server = server;
		}

		public FtpClient(string username,
		                 string password)
			: this()
		{
			this.Username = username;
			this.Password = password;
		}

		public FtpClient(Uri uri)
			: this()
		{
			var uriBuilder = new UriBuilder(uri);

			this.Username = uriBuilder.UserName;
			this.Password = uriBuilder.Password;
			this.Server = uriBuilder.Host;
			this.Port = uriBuilder.Port;
		}

		public FtpClient()
		{
			this.Encoding = Encoding.UTF8;
			this.ConnectTimeout = TimeSpan.FromSeconds(30);
			this.ReceiveTimeout = TimeSpan.FromSeconds(30);
			this.SendTimeout = TimeSpan.FromMinutes(5);
			this.SocketClientAccessPolicyProtocol = SocketClientAccessPolicyProtocol.Http;

			this._ftpFeatures = new Lazy<FtpFeatures>(() =>
			{
				var ftpReply = this.Execute("FEAT");
				if (!ftpReply.Success)
				{
					return FtpFeatures.Unknown;
				}

				var messages = ftpReply.Messages;
				var ftpFeatures = FtpClientHelper.ParseFtpFeatures(messages);

				return ftpFeatures;
			});
		}

		public FtpFeatures FtpFeatures
		{
			get
			{
				return this._ftpFeatures.Value;
			}
		}

		public Encoding Encoding { get; set; }
		public TimeSpan ConnectTimeout { get; set; }
		public TimeSpan ReceiveTimeout { get; set; }
		public TimeSpan SendTimeout { get; set; }
		public string Server { get; set; }
		public string Username { get; set; }
		public string Password { get; set; }
		public SocketClientAccessPolicyProtocol SocketClientAccessPolicyProtocol { get; set; }
		public int Port { get; set; }

		public void Dispose()
		{
			{
				var controlComplexSocket = this._controlComplexSocket;
				if (controlComplexSocket != null)
				{
					controlComplexSocket.Dispose();
				}
			}
		}

		public event EventHandler<BaseFtpCommandFailedEventArgs> FtpCommandFailed;

		internal void RaiseFtpCommandFailedAsync(BaseFtpCommandFailedEventArgs e)
		{
			var handler = this.FtpCommandFailed;
			if (handler != null)
			{
				Task.Factory.StartNew(() => handler.Invoke(this,
				                                           e));
			}
		}

		public IEnumerable<FtpListItem> GetListing(string path)
		{
			IEnumerable<string> rawListing;
			FtpListType ftpListType;

			lock (this._lockControlComplexSocket)
			{
				{
					var success = this.EnsureConnection();
					if (!success)
					{
						return Enumerable.Empty<FtpListItem>();
					}
				}

				string command;
				if (this.FtpFeatures.HasFlag(FtpFeatures.MLSD))
				{
					ftpListType = FtpListType.MLSD;
					command = "MLSD";
				}
				else if (this.FtpFeatures.HasFlag(FtpFeatures.MLST))
				{
					ftpListType = FtpListType.MLST;
					command = "MLST";
				}
				else
				{
					// TODO check if really *always* available
					ftpListType = FtpListType.LIST;
					command = "LIST";
				}

				var concreteCommand = string.Format("{0} {1}",
				                                    command,
				                                    path);

				{
					var transferComplexSocket = this.GetPassiveComplexSocket();
					if (transferComplexSocket == null)
					{
						return Enumerable.Empty<FtpListItem>();
					}

					using (transferComplexSocket)
					{
						{
							var ftpReply = this.Execute(() => transferComplexSocket.Connect(this.ConnectTimeout),
							                            concreteCommand);
							if (!ftpReply.Success)
							{
								return Enumerable.Empty<FtpListItem>();
							}
						}

						using (var socketAsyncEventArgs = transferComplexSocket.GetSocketAsyncEventArgs(this.ReceiveTimeout))
						{
							var ftpReply = transferComplexSocket.Socket.Receive(socketAsyncEventArgs,
							                                                    this.Encoding);
							rawListing = ftpReply.Messages;
						}
					}

					{
						var ftpReply = this._controlComplexSocket.GetFinalFtpReply(this.Encoding,
						                                                           this.ReceiveTimeout);
						var success = ftpReply.Success;
						if (!success)
						{
							return Enumerable.Empty<FtpListItem>();
						}
					}
				}
			}

			var ftpListItems = FtpListItem.ParseList(rawListing,
			                                         ftpListType);

			return ftpListItems;
		}

		public bool CreateDirectory(string path)
		{
			lock (this._lockControlComplexSocket)
			{
				{
					var success = this.EnsureConnection();
					if (!success)
					{
						return false;
					}
				}
				{
					FtpReply ftpReply;
					var success = this.TryCreateDirectoryInternal(path,
					                                              out ftpReply);
					return success;
				}
			}
		}

		private bool TryCreateDirectoryInternal(string path,
		                                        out FtpReply ftpReply)
		{
			ftpReply = this.Execute("MKD {0}",
			                        path);
			return ftpReply.Success;
		}

		public bool Upload(Stream stream,
		                   FtpFile ftpFile,
		                   bool createDirectoryIfNotExists = true)
		{
			lock (this._lockControlComplexSocket)
			{
				{
					var success = this.EnsureConnection();
					if (!success)
					{
						return false;
					}
				}

				var controlComplexSocket = this._controlComplexSocket;
				{
					var success = this.GotoDirectory(ftpFile,
					                                 createDirectoryIfNotExists);
					if (!success)
					{
						return false;
					}
				}

				var transferComplexSocket = this.GetPassiveComplexSocket();
				if (transferComplexSocket == null)
				{
					return false;
				}

				using (transferComplexSocket)
				{
					var ftpReply = this.Execute(() => transferComplexSocket.Connect(this.ConnectTimeout),
					                            "STOR {0}",
					                            ftpFile.Name);
					if (!ftpReply.Success)
					{
						return false;
					}

					{
						// sending content via transfer socket
						var success = transferComplexSocket.Send(stream,
						                                         this.SendTimeout);
						if (!success)
						{
							return false;
						}
					}
				}

				{
					var ftpReply = controlComplexSocket.GetFinalFtpReply(this.Encoding,
					                                                     this.ReceiveTimeout);
					var success = ftpReply.Success;

					return success;
				}
			}
		}

		private bool EnsureConnection()
		{
			this._controlComplexSocket = this._controlComplexSocket ?? this.CreateControlComplexSocket();
			if (this._controlComplexSocket.Connected)
			{
				return true;
			}

			lock (this._lockControlComplexSocket)
			{
				{
					var success = this._controlComplexSocket.Connect(this.ConnectTimeout);
					if (!success)
					{
						this._controlComplexSocket = null;
						return false;
					}
				}
				using (var socketAsyncEventArgs = this._controlComplexSocket.GetSocketAsyncEventArgs(this.ReceiveTimeout))
				{
					var complexResult = this._controlComplexSocket.Socket.Receive(socketAsyncEventArgs,
					                                                              this.Encoding);
					var success = complexResult.Success;
					if (!success)
					{
						this._controlComplexSocket = null;
						return false;
					}
				}
				{
					var ftpReply = this.Execute("USER {0}",
					                            this.Username);
					if (!ftpReply.Success)
					{
						var message = string.Format("Could not authenticate with USER '{0}'",
						                            this.Username);
						var ftpAuthenticationFailedEventArgs = new FtpAuthenticationFailedEventArgs(message);
						this.RaiseFtpCommandFailedAsync(ftpAuthenticationFailedEventArgs);
					}
					else if (ftpReply.FtpResponseType == FtpResponseType.PositiveIntermediate)
					{
						ftpReply = this.Execute("PASS {0}",
						                        this.Password);
						if (!ftpReply.Success)
						{
							var message = string.Format("Could not authenticate with USER '{0}' and PASS '{1}'",
							                            this.Username,
							                            this.Password);
							var ftpAuthenticationFailedEventArgs = new FtpAuthenticationFailedEventArgs(message);
							this.RaiseFtpCommandFailedAsync(ftpAuthenticationFailedEventArgs);
						}
					}
					if (!ftpReply.Success)
					{
						this._controlComplexSocket = null;
						return false;
					}
				}
			}

			return true;
		}

		private ComplexSocket GetPassiveComplexSocket()
		{
			var ftpReply = this.Execute("PASV");
			if (!ftpReply.Success)
			{
				return null;
			}

			var matches = Regex.Match(ftpReply.ResponseMessage,
			                          "([0-9]+),([0-9]+),([0-9]+),([0-9]+),([0-9]+),([0-9]+)");
			if (!matches.Success)
			{
				return null;
			}
			if (matches.Groups.Count != 7)
			{
				return null;
			}

			var ipAddress = FtpClientHelper.ParseIPAddress(from index in Enumerable.Range(1,
			                                                                              4)
			                                               let octet = matches.Groups[index].Value
			                                               select octet);
			var p1 = matches.Groups[5].Value;
			var p2 = matches.Groups[6].Value;
			var port = FtpClientHelper.ParsePassivePort(p1,
			                                            p2);
			var transferComplexSocket = this.CreateTransferComplexSocket(ipAddress,
			                                                             port);
			return transferComplexSocket;
		}

		public FtpReply Execute(string command,
		                        params object[] args)
		{
			var ftpReply = this.Execute(null,
			                            command,
			                            args);

			return ftpReply;
		}

		public FtpReply Execute(Func<bool> interimPredicate,
		                        string command,
		                        params object[] args)
		{
			command = string.Format(command,
			                        args);

			var controlComplexSocket = this._controlComplexSocket;

			{
				var success = controlComplexSocket.Send(command,
				                                        this.Encoding,
				                                        this.SendTimeout);
				if (!success)
				{
					return FtpReply.FailedFtpReply;
				}
			}
			if (interimPredicate != null)
			{
				var success = interimPredicate.Invoke();
				if (!success)
				{
					return FtpReply.FailedFtpReply;
				}
			}
			using (var socketAsyncEventArgs = controlComplexSocket.GetSocketAsyncEventArgs(this.ReceiveTimeout))
			{
				var ftpReply = controlComplexSocket.Socket.Receive(socketAsyncEventArgs,
				                                                   this.Encoding);
				return ftpReply;
			}
		}

		private bool GotoDirectory(FtpFile ftpFile,
		                           bool createDirectoryIfNotExists)
		{
			var hierarchy = ftpFile.GetHierarchy()
			                       .Reverse();

			foreach (var element in hierarchy)
			{
				var name = element.Name;
				var ftpReply = this.Execute("CWD {0}",
				                            name);
				if (!ftpReply.Success)
				{
					return false;
				}

				switch (ftpReply.FtpResponseType)
				{
					case FtpResponseType.PermanentNegativeCompletion:
						// TODO some parsing of the actual FtpReply.ResponseCode should be done in here. i assume 5xx-state means "directory does not exist" all the time, which might be wrong
						var success = createDirectoryIfNotExists && this.TryCreateDirectoryInternal(name,
						                                                                            out ftpReply);
						if (!success)
						{
							return false;
						}
						goto case FtpResponseType.PositiveCompletion;
					case FtpResponseType.PositiveCompletion:
						continue;
					default:
						return false;
				}
			}
			return true;
		}
	}
}
