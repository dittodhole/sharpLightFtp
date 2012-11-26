﻿using System.Collections.Generic;
using System.Linq;

namespace sharpLightFtp
{
	internal sealed class ComplexResult
	{
		internal static readonly ComplexResult FailedComplexResult = new ComplexResult(FtpResponseType.None, 0, null, Enumerable.Empty<string>());

		private readonly FtpResponseType _ftpResponseType;
		private readonly List<string> _messages = new List<string>();
		private readonly int _responseCode;
		private readonly string _responseMessage;

		internal ComplexResult(FtpResponseType ftpResponseType, int responseCode, string responseMessage, IEnumerable<string> messages)
		{
			this._ftpResponseType = ftpResponseType;
			this._responseCode = responseCode;
			this._responseMessage = responseMessage;
			this._messages.AddRange(messages);
		}

		internal int ResponseCode
		{
			get
			{
				return this._responseCode;
			}
		}

		internal string ResponseMessage
		{
			get
			{
				return this._responseMessage;
			}
		}

		internal IEnumerable<string> Messages
		{
			get
			{
				return this._messages;
			}
		}

		internal FtpResponseType FtpResponseType
		{
			get
			{
				return this._ftpResponseType;
			}
		}

		internal bool Success
		{
			get
			{
				switch (this.FtpResponseType)
				{
					case FtpResponseType.PositiveIntermediate:
					case FtpResponseType.PositiveCompletion:
					case FtpResponseType.PositivePreliminary:
						return true;
					default:
						return false;
				}
			}
		}
	}
}
