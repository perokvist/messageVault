using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MessageVault {

    public sealed class MessageReader : IDisposable {
        readonly ICheckpointReader _position;
        readonly IPageReader _messages;

        readonly byte[] _buffer;
        const int Limit = 1024*1024*4;

        public MessageReader(ICheckpointReader position, IPageReader messages) {
            _position = position;
            _messages = messages;
            _buffer = new byte[Limit];
        }

        public long GetPosition() {
            return _position.Read();
        }

        public MessageResult ReadMessages(long from, long till, int maxCount) {
            Require.ZeroOrGreater("from", from);
            Require.ZeroOrGreater("maxOffset", till);
            Require.Positive("maxCount", maxCount);

            var list = new List<Message>(maxCount);
            var position = from;

            using (var prs = new PageReadStream(_messages, from, till, _buffer)) {
                using (var bin = new BinaryReader(prs)) {
                    while (prs.Position < prs.Length) {
                        var message = MessageFormat.ReadMessage(bin);
                        list.Add(message);
                        position = prs.Position;
                        if (list.Count >= maxCount) {
                            break;
                        }
                    }
                }
            }
            return new MessageResult(list, position);
        }

        public ConcurrentQueue<Message> Subscribe(
            CancellationToken ct,
            long start,
            int bufferSize,
            int cacheSize
            ) {
            var queue = new ConcurrentQueue<Message>();

            Task.Factory.StartNew(() => RunSubscription(queue, start, ct, bufferSize, cacheSize),
                TaskCreationOptions.LongRunning);
            return queue;
        }

        void RunSubscription(
            ConcurrentQueue<Message> queue,
            long startPosition,
            CancellationToken ct,
            int bufferSize,
            int queueLimit
            ) {

            var buffer = new byte[bufferSize];
			var currentPosition = startPosition;

            // read messages until token cancelled
            while (!ct.IsCancellationRequested) {
                try {
	                currentPosition = EnqueAllExistingMessages( queue, currentPosition, ct, buffer, queueLimit );
	                if( WaitForNewMessagesInMv( currentPosition, ct ) )
					{
						// Cancellation signaled - exit
		                return;
					}
                }
                catch (Exception ex) {
                    Debug.Print("Exception {0}", ex);
                    ct.WaitHandle.WaitOne(1000*20);
                }
            }
        }

	    bool WaitForNewMessagesInMv( long startPosition, CancellationToken ct ) {
		    // wait for the message stream to get new message
		    while( _position.Read() == startPosition ) {
			    if( ct.WaitHandle.WaitOne( 1000 ) ) {
				    // Cancellation signaled - exit
				    return true;
			    }
		    }
		    return false;
	    }

	    long EnqueAllExistingMessages( ConcurrentQueue< Message > queue, long position, CancellationToken ct, byte[] buffer, int cacheSize ) {
			// read current max length
		    var length = _position.Read();
		    using( var prs = new PageReadStream( _messages, position, length, buffer ) ) {
			    using( var bin = new BinaryReader( prs ) ) {
				    while( prs.Position < prs.Length ) {
					    position = EnqueMessage( queue, bin, prs );

					    WaitForMessagesToDequeue( queue, ct, cacheSize );
				    }
			    }
		    }
		    return position;
	    }

	    static long EnqueMessage( ConcurrentQueue< Message > queue, BinaryReader bin, PageReadStream prs ) {
		    var message = MessageFormat.ReadMessage( bin );
		    queue.Enqueue( message );
		    return prs.Position;
	    }

	    static void WaitForMessagesToDequeue( ConcurrentQueue< Message > queue, CancellationToken ct, int queueLimit ) {
		    while( queue.Count >= queueLimit ) {
			    ct.WaitHandle.WaitOne( 100 );
		    }
	    }

	    public async Task<MessageResult> GetMessagesAsync(CancellationToken ct, long start,
            int limit) {
            while (!ct.IsCancellationRequested) {
                var actual = _position.Read();
                if (actual < start) {
                    var msg = string.Format("Actual stream length is {0}, but requested {1}", actual,
                        start);
                    throw new InvalidOperationException(msg);
                }
                if (actual == start) {
                    await Task.Delay(1000, ct);
                    continue;
                }
                var result = await Task.Run(() => ReadMessages(start, actual, limit));

                return result;
            }
            return MessageResult.Empty(start);
        }

        bool _disposed;

        public void Dispose() {
            if (_disposed) {
                return;
            }
            using (_messages) {
                using (_position) {
                    _disposed = true;
                }
            }
        }
    }
}