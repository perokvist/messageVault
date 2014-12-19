using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MessageVault.Cloud;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Linq;

namespace MessageVault {



	public sealed class MessageReader {
		readonly CloudCheckpointReader _position;
		readonly PageReader _messages;


		public static MessageReader Create(string sas) {
			var uri = new Uri(sas);
			var container = new CloudBlobContainer(uri);

			var posBlob = container.GetPageBlobReference(Constants.PositionFileName);
			var dataBlob = container.GetPageBlobReference(Constants.StreamFileName);
			var position = new CloudCheckpointReader(posBlob);
			var messages = new PageReader(dataBlob);
			return new MessageReader(position, messages);

		}

		public MessageReader(CloudCheckpointReader position, PageReader messages) {
			_position = position;
			_messages = messages;
		}


		public long GetPosition() {
			// TODO: inline readers
			return _position.Read();
		}
		
		public async Task<MessageResult> GetMessagesAsync(CancellationToken ct, long start, int limit) {

			while (!ct.IsCancellationRequested) {
				var actual = _position.Read();
				if (actual < start) {
					var msg = string.Format("Actual stream length is {0}, but requested {1}", actual, start);
					throw new InvalidOperationException(msg);
				}
				if (actual == start) {
					await Task.Delay(1000, ct);
					continue;
				}
				var result = await Task.Run(() => _messages.ReadMessages(start, actual, limit));
				
				return result;

			}
			return MessageResult.Empty(start);
		} 
	}

}