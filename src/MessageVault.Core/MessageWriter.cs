using System;
using System.Collections.Generic;
using System.IO;
using System.Text;


namespace MessageVault {

	/// <summary>
	/// Writes messages as a stream to an underlying <see cref="IPageWriter"/>, 
	/// using provided <see cref="ICheckpointWriter"/> to mark commits.
	/// </summary>
	public sealed class MessageWriter : IDisposable{
		
		readonly IPageWriter _pageWriter;
		readonly ICheckpointWriter _positionWriter;

		long _savedPosition;
		readonly int _pageSize;

		readonly byte[] _buffer;
		readonly MemoryStream _cache;
		readonly BinaryWriter _binary;

		public MessageWriter(IPageWriter pageWriter, ICheckpointWriter positionWriter) {
			_pageWriter = pageWriter;
			_positionWriter = positionWriter;
		
			_buffer = new byte[pageWriter.GetMaxCommitSize()];
			_pageSize = pageWriter.GetPageSize();
			_cache = new MemoryStream(_buffer, true);
			_binary = new BinaryWriter(_cache, Encoding.UTF8, true);
		}

		public long GetPosition() {
			return _savedPosition;
		}
		public int GetBufferSize()
		{
			return _buffer.Length;
		}

		public void Init() {
			_pageWriter.Init();
			_savedPosition = _positionWriter.GetOrInitPosition();

			//_log.Verbose("Stream {stream} at {offset}", _streamName, _position);

			var tail = OffsetInLastPage(_savedPosition);
			if (tail != 0) {
				// preload tail

				var offset = LastFullPageTail(_savedPosition);
				//_log.Verbose("Load tail at {offset}", offset);
				var page = _pageWriter.ReadPage(offset);
				_cache.Write(page, 0, tail);
			}
		}

		long FullPagesSize(long position) {
			var tail = OffsetInLastPage(position);
			if (tail == 0) {
				return position;
			}
			return position - tail + _pageSize;
		}

		/// <summary>
		/// Calculates portion of bytes in value that can fill up entire page.
		/// </summary>
		/// <param name="position">The value.</param>
		/// <returns></returns>
		long LastFullPageTail(long position) {
			return position - OffsetInLastPage(position);
		}

		int OffsetInLastPage(long position) {
			return (int) (position % _pageSize);
		}

		/// <summary>
		/// Position after all data, including both saved and cached (which wasn't saved yet).
		/// </summary>
		/// <returns>Position of the next message to be added to the stream.</returns>
		long VirtualPosition() {
			return LastFullPageTail(_savedPosition) + _cache.Position;
		}

		long BufferStarts() {
			return LastFullPageTail(_savedPosition);
		}
		long DataStarts() {
			return _savedPosition;
		}
		long DataEnds() {
			return BufferStarts() + _cache.Position;
		}
		long BufferEnds() {
			return BufferStarts() + FullPagesSize(_cache.Position);
		}

		void FlushBuffer() {
			var bytesToWrite = _cache.Position;
			
			//Log.Verbose("Flush Buffer {3}-[{0} ({2}) {1}]-{4}", 
			//	DataStarts(), 
			//	DataEnds(), 
			//	DataEnds() - DataStarts(),
			//	BufferStarts(),
			//	BufferEnds());

			// Ensure we have enough space to write everything in buffer
			var newPosition = EnsureEnoughSpaceToWriteCachedMessages();

			// how many bytes do we need to write, rounded up to pages
			var pageBytesToWrite = (int) FullPagesSize(_cache.Position);

			SaveBuffer( pageBytesToWrite );

			_savedPosition = newPosition;
			// we write so little, that our page tail didn't change
			if (bytesToWrite < _pageSize) {
				return;
			}
			// grab the tail of the stream (up to the page size)
			// and keep it for future writes
			var offsetInLastSavedPage = OffsetInLastPage(bytesToWrite);

			ClearCacheKeepingDataForTheLastUnfilledPage( offsetInLastSavedPage, bytesToWrite );
		}

		void ClearCacheKeepingDataForTheLastUnfilledPage( int offsetInLastSavedPage, long bytesSaved ) {
			if( offsetInLastSavedPage == 0 ) {
				// our position is at the page boundary, nothing to keep
//				Array.Clear( _buffer, 0, _buffer.Length );
				_cache.Seek( 0, SeekOrigin.Begin );
			} else {
				// we need to save the data in the last page in memory
				// this will allow us to append data in the same page and rewrite page since need save one page at a time
				Array.Copy( _buffer, LastFullPageTail( bytesSaved ), _buffer, 0, _pageSize ); // copy data in last not fully filled page to the start of the buffer
//				Array.Clear( _buffer, _pageSize, _buffer.Length - _pageSize ); // clear the rest of the buffer
				_cache.Seek( offsetInLastSavedPage, SeekOrigin.Begin );
			}
		}

		void SaveBuffer( int pageBytesToWrite ) {
			// Write all data out of buffer, including the empty tail (if present)
			// starting from the _savedPosition, rounded down to pages
			using( var copy = new MemoryStream( _buffer, 0, pageBytesToWrite ) ) {
				// Calculate the start position of the first page that wasn't fully filled in storage
				// This means we will overwrite a page on each save until the page gets completelly filled and only then move on to the next page
				var firstUnfilledPagePosition = LastFullPageTail( _savedPosition );
				_pageWriter.Save( copy, firstUnfilledPagePosition );
			}
		}

		long EnsureEnoughSpaceToWriteCachedMessages() {
			var newPosition = VirtualPosition();
			_pageWriter.EnsureSize( FullPagesSize( newPosition ) );
			return newPosition;
		}

		public long Append(ICollection<MessageToWrite> messages) {
			if (messages.Count == 0) {
				throw new ArgumentException("Must provide non-empty array", "messages");
			}
			foreach (var item in messages) {
				if (item.Value.Length > Constants.MaxMessageSize) {
					string message = "Each message must be smaller than " + Constants.MaxMessageSize;
					throw new InvalidOperationException(message);
				}

				if (item.Key.Length > Constants.MaxContractLength) {
					var message = "Each contract must be shorter than " + Constants.MaxContractLength;
					throw new InvalidOperationException(message);
				}

				var sizeEstimate = MessageFormat.EstimateMessageSize(item);

				var availableInBuffer = _cache.Length - _cache.Position;
				if (sizeEstimate > availableInBuffer) {
					FlushBuffer();
				}

				var offset = VirtualPosition();
				var id = MessageId.CreateNew(offset);
				MessageFormat.WriteMessage(_binary, id, item);
			}
			FlushBuffer();
			_positionWriter.Update(_savedPosition);
			return _savedPosition;
		}

	    bool _disposed;
	    public void Dispose() {
	        if (_disposed) {
	            return;
	        }
            using (_positionWriter)
            using (_pageWriter) {
                _disposed = true;
            }

	    }
	}
}