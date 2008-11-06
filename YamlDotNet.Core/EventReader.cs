﻿using System;
using System.IO;
using YamlDotNet.Core;
using System.Globalization;
using Event = YamlDotNet.Core.Events.ParsingEvent;

namespace YamlDotNet.Core
{
	/// <summary>
	/// Reads events from a sequence of <see cref="Event" />.
	/// </summary>
	public class EventReader
	{
		private readonly Parser parser;
		private bool endOfStream;

		/// <summary>
		/// Initializes a new instance of the <see cref="EventReader"/> class.
		/// </summary>
		/// <param name="parser">The parser that provides the events.</param>
		public EventReader(Parser parser)
		{
			this.parser = parser;
			MoveNext();
		}

		/// <summary>
		/// Ensures that the current event is of the specified type, returns it and moves to the next event.
		/// </summary>
		/// <typeparam name="T">Type of the <see cref="Event"/>.</typeparam>
		/// <returns>Returns the current event.</returns>
		/// <exception cref="YamlException">If the current event is not of the specified type.</exception>
		public T Expect<T>() where T : Event
		{
			if (!Accept<T>())
			{
				// TODO: Throw a better exception
				throw new YamlException(
				    string.Format(
				        CultureInfo.InvariantCulture,
				        "Expected '{0}', got '{1}'.",
				        typeof(T).Name,
				        parser.Current.GetType().Name
				    )
				);
			}
			T yamlEvent = (T)parser.Current;
			MoveNext();
			return yamlEvent;
		}

		/// <summary>
		/// Moves to the next event.
		/// </summary>
		private void MoveNext()
		{
			endOfStream = !parser.MoveNext();
		}

		/// <summary>
		/// Checks whether the current event is of the specified type.
		/// </summary>
		/// <typeparam name="T">Type of the event.</typeparam>
		/// <returns>Returns true if the current event is of type <typeparamref name="T"/>. Otherwise returns false.</returns>
		public bool Accept<T>() where T : Event
		{
			EnsureNotAtEndOfStream();

			return parser.Current is T;
		}

		/// <summary>
		/// Throws an exception if Ensures the not at end of stream.
		/// </summary>
		private void EnsureNotAtEndOfStream()
		{
			if (endOfStream)
			{
				throw new EndOfStreamException();
			}
		}
	}
}