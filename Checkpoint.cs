using System;
using Azure;
using Microsoft.Azure.Cosmos.Table;

namespace nsgFlowLoggingSplunk
{
    public class Checkpoint : TableEntity
    {
        //added to implement ITableEntity
        public int CheckpointIndex { get; set; }  // index of the last processed block list item
        public string PartitionKey { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public string RowKey { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public DateTimeOffset? Timestamp { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public ETag ETag { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        //end added to implement ITableEntity
        public Checkpoint()
        {
        }

        public Checkpoint(string partitionKey, string rowKey, string blockName, long offset, int index)
        {
            PartitionKey = partitionKey;
            RowKey = rowKey;
            CheckpointIndex = index;
        }

        public static Checkpoint GetCheckpoint(BlobDetails blobDetails, CloudTable checkpointTable)
        {
            TableOperation operation = TableOperation.Retrieve<Checkpoint>(
                blobDetails.GetPartitionKey(), blobDetails.GetRowKey());
            TableResult result = checkpointTable.ExecuteAsync(operation).Result;

            Checkpoint checkpoint = (Checkpoint)result.Result;

            if (checkpoint == null)
            {
                checkpoint = new Checkpoint(blobDetails.GetPartitionKey(), blobDetails.GetRowKey(), "", 0, 1);
            }
            if (checkpoint.CheckpointIndex == 0)
            {
                checkpoint.CheckpointIndex = 1;
            }

            return checkpoint;
        }

        public void PutCheckpoint(CloudTable checkpointTable, int index)
        {
            CheckpointIndex = index;
            TableOperation operation = TableOperation.InsertOrReplace(this);
            checkpointTable.ExecuteAsync(operation).Wait();
        }
    }
}
