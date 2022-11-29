using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Azure.Data.Tables;
using System.Threading.Tasks;
using System.Buffers;
using Azure.Data.Tables.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Azure.Storage.Blob;
using System.Linq;
using Microsoft.Azure.Cosmos.Table;

namespace nsgFlowLoggingSplunk
{
    public static class BlobTriggerIngestAcndTransmit
    {
        [FunctionName("BlobTriggerIngestAndTransmit")]
        public static async Task Run(
            [BlobTrigger("%blobContainerName%/resourceId=/SUBSCRIPTIONS/{subId}/RESOURCEGROUPS/{resourceGroup}/PROVIDERS/MICROSOFT.NETWORK/NETWORKSECURITYGROUPS/{nsgName}/y={blobYear}/m={blobMonth}/d={blobDay}/h={blobHour}/m={blobMinute}/macAddress={mac}/PT1H.json", Connection = "%nsgSourceDataAccount%")] CloudBlockBlob myBlob,//Stream myBlob, //BlobClient myBlob,
            [Table("checkpoints", Connection = "AzureWebJobsStorage")] CloudTable checkpointTable,
            Binder nsgDataBlobBinder,
            Binder cefLogBinder,
            string subId, string resourceGroup, string nsgName, string blobYear, string blobMonth, string blobDay, string blobHour, string blobMinute, string mac,
            ExecutionContext executionContext,
            ILogger log)
        {
            log.LogDebug($"BlobTriggerIngestAndTransmit triggered: {executionContext.InvocationId} ");

            string nsgSourceDataAccount = Util.GetEnvironmentVariable("nsgSourceDataAccount");
            if (nsgSourceDataAccount.Length == 0)
            {
                log.LogError("Value for nsgSourceDataAccount is required.");
                throw new System.ArgumentNullException("nsgSourceDataAccount", "Please provide setting.");
            }

            string blobContainerName = Util.GetEnvironmentVariable("blobContainerName");
            if (blobContainerName.Length == 0)
            {
                log.LogError("Value for blobContainerName is required.");
                throw new System.ArgumentNullException("blobContainerName", "Please provide setting.");
            }

            string outputBinding = Util.GetEnvironmentVariable("outputBinding");
            if (outputBinding.Length == 0)
            {
                log.LogError("Value for outputBinding is required. Permitted values are: 'arcsight', 'splunk', 'eventhub'.");
                throw new System.ArgumentNullException("outputBinding", "Please provide setting.");
            }

            var blobDetails = new BlobDetails(subId, resourceGroup, nsgName, blobYear, blobMonth, blobDay, blobHour, blobMinute, mac);
            // get checkpoint
            Checkpoint checkpoint = Checkpoint.GetCheckpoint(blobDetails, checkpointTable);
           /* // get checkpoint - modified Azure.Data.Tables
            string tableName = "checkpoints";
            var tableServiceClient = new TableServiceClient(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));
            TableItem checkpointTable = tableServiceClient.CreateTableIfNotExists(tableName);
            var tableClient = tableServiceClient.GetTableClient(tableName);

            //[Table("checkpoints", Connection = "AzureWebJobsStorage")] CloudTable checkpointTable,
            Checkpoint checkpoint = Checkpoint.GetCheckpoint(blobDetails, checkpointTable, tableClient);
           */
            var blockList = myBlob.DownloadBlockListAsync().Result;
            
            var startingByte = blockList.Where((item, index) => index < checkpoint.CheckpointIndex).Sum(item => item.Length);
            var endingByte = blockList.Where((item, index) => index < blockList.Count() - 1).Sum(item => item.Length);
            var dataLength = endingByte - startingByte;

            log.LogDebug("Blob: {0}, starting byte: {1}, ending byte: {2}, number of bytes: {3}", blobDetails.ToString(), startingByte, endingByte, dataLength);

            if (dataLength == 0)
            {
                log.LogWarning(string.Format("Blob: {0}, triggered on completed hour.", blobDetails.ToString()));
                return;
            }
            //foreach (var item in blockList)
            //{
            //    log.LogInformation("Name: {0}, Length: {1}", item.Name, item.Length);
            //}

            var attributes = new Attribute[]
            {
                new BlobAttribute(string.Format("{0}/{1}", blobContainerName, myBlob.Name)),
                new StorageAccountAttribute(nsgSourceDataAccount)
            };

            string nsgMessagesString = "";
            var bytePool = ArrayPool<byte>.Shared;
            byte[] nsgMessages = bytePool.Rent((int)dataLength);
            try
            {
                CloudBlockBlob blob = nsgDataBlobBinder.BindAsync<CloudBlockBlob>(attributes).Result;
                await blob.DownloadRangeToByteArrayAsync(nsgMessages, 0, startingByte, dataLength);

                //BlobServiceClient blobServiceClient = new BlobServiceClient(nsgSourceDataAccount);
                //BlockBlobClient blockBlobClient = nsgDataBlobBinder.BindAsync<BlockBlobClient>(attributes).Result;
                //await blockBlobClient


                if (nsgMessages[0] == ',')
                {
                    nsgMessagesString = System.Text.Encoding.UTF8.GetString(nsgMessages, 1, (int)(dataLength - 1));
                }
                else
                {
                    nsgMessagesString = System.Text.Encoding.UTF8.GetString(nsgMessages, 0, (int)dataLength);
                }
            }
            catch (Exception ex)
            {
                log.LogError(string.Format("Error binding blob input: {0}", ex.Message));
                throw ex;
            }
            finally
            {
                bytePool.Return(nsgMessages);
            }

            //log.LogDebug(nsgMessagesString);


            try
            {
                int bytesSent = await Util.SendMessagesDownstreamAsync(nsgMessagesString, executionContext, cefLogBinder, log);
                log.LogInformation($"Sending {nsgMessagesString.Length} bytes (denormalized to {bytesSent} bytes) downstream via output binding {outputBinding}.");
            }
            catch (Exception ex)
            {
                log.LogError(string.Format("SendMessagesDownstreamAsync: Error {0}", ex.Message));
                throw ex;
            }

            checkpoint.PutCheckpoint(checkpointTable, blockList.Count() - 1);
        }
    }
}
