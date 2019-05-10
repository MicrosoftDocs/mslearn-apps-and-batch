namespace cutifypets
{
	using System;
	using Microsoft.Azure.Batch;
	using Microsoft.Azure.Batch.Auth;
	using Microsoft.Azure.Batch.Common;
	using System.Threading.Tasks;
	using System.Collections.Generic;
	using Microsoft.Azure.Storage.Blob;
	using Microsoft.Azure.Storage;
	using System.IO;
	using System.Linq;

	class Program
	{
		// Update the Batch and Storage account credential strings below with the values unique to your accounts.
		// These are used when constructing connection strings for the Batch and Storage client objects.
		private const string envVarBatchURI = "BATCH_URL";
		private const string envVarBatchName = "BATCH_NAME";
		private const string envVarKey = "BATCH_KEY";
		private static string batchAccountName;
		private static string batchAccountUrl;
		private static string batchAccountKey;
		
		// Pool and Job constants
		private const string PoolId = "WinFFmpegPoolAB";
		private const int DedicatedNodeCount = 0;
		private const int LowPriorityNodeCount = 3;
		private const string PoolVMSize = "STANDARD_D2_v2";
		private const string JobId = "WinFFmpegJob";
		
		// Application package Id and version
		// Complete exercise 3 to setup this application in your Batch account.
		private const string appPackageId = "ffmpeg";
		private const string appPackageVersion = "3.4";
		
		// Storage account credentials
		private const string envVarStorage = "STORAGE_NAME";
		private const string envVarStorageKey = "STORAGE_KEY";
		private static string storageAccountName;
		private static string storageAccountKey;
		
		/// <summary>
		/// Provides an asynchronous version of the Main method, allowing for the awaiting of async method calls within.
		/// </summary>
		/// <returns>A <see cref = "System.Threading.Tasks.Task"/> object that represents the asynchronous operation.</returns>
		static async Task Main(string[] args)
		{
			// Read the environment variables to allow the app to connect to the Azure Batch and Azure Storage accounts
			batchAccountUrl = Environment.GetEnvironmentVariable(envVarBatchURI);
			batchAccountName = Environment.GetEnvironmentVariable(envVarBatchName);
			batchAccountKey = Environment.GetEnvironmentVariable(envVarKey);
			storageAccountName = Environment.GetEnvironmentVariable(envVarStorage);
			storageAccountKey = Environment.GetEnvironmentVariable(envVarStorageKey);
			
			// Show the user the accounts they are attaching to
			Console.WriteLine("BATCH URL: {0}, Name: {1}, Key: {2}", batchAccountUrl, batchAccountName, batchAccountKey);
			Console.WriteLine("Storage Name: {0}, Key: {1}", storageAccountName, storageAccountKey);
			
			// Construct the Storage account connection string
			string storageConnectionString = String.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}", storageAccountName, storageAccountKey);
			
			// Retrieve the storage account
			CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageConnectionString);
			
			// Create the blob client, for use in obtaining references to blob storage containers
			CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
			
			// Use the blob client to create the containers in blob storage
			const string inputContainerName = "input";
			const string outputContainerName = "output";
			await CreateContainerIfNotExistAsync(blobClient, inputContainerName);
			await CreateContainerIfNotExistAsync(blobClient, outputContainerName);
			
			// RESOURCE FILE SETUP
			// Add *.mp4 files into the \<solutiondir>\InputFiles folder.
			string inputPath = Path.Combine(Environment.CurrentDirectory, "InputFiles");
			List<string> inputFilePaths = new List<string>(Directory.GetFileSystemEntries(inputPath, "*.mp4", SearchOption.TopDirectoryOnly));
			
			// Upload data files.
			// Upload the data files using UploadResourceFilesToContainer(). This data will be
			// processed by each of the tasks that are executed on the compute nodes within the pool.
			List<ResourceFile> inputFiles = await UploadFilesToContainerAsync(blobClient, inputContainerName, inputFilePaths);
			
			// Obtain a shared access signature that provides write access to the output container to which
			// the tasks will upload their output.
			string outputContainerSasUrl = GetContainerSasUrl(blobClient, outputContainerName, SharedAccessBlobPermissions.Write);
			
			// The batch client requires a BatchSharedKeyCredentials object to open a connection
			var sharedKeyCredentials = new BatchSharedKeyCredentials(batchAccountUrl, batchAccountName, batchAccountKey);
			using (BatchClient batchClient = BatchClient.Open(sharedKeyCredentials))
			{
				// Create the Batch pool, which contains the compute nodes that execute the tasks.
				await CreateBatchPoolAsync(batchClient, PoolId);
				
				// Create the job that runs the tasks.
				await CreateJobAsync(batchClient, JobId, PoolId);
				
				// Create a collection of tasks and add them to the Batch job. 
				// Provide a shared access signature for the tasks so that they can upload their output
				// to the Storage container.
				List<CloudTask> runningTasks = await AddTasksAsync(batchClient, JobId, inputFiles, outputContainerSasUrl);
				
				// Monitor task success or failure, specifying a maximum amount of time to wait for
				// the tasks to complete.
				await MonitorTasksAsync(batchClient, JobId, TimeSpan.FromMinutes(30));
				
				// Delete input container in storage
				Console.WriteLine("Deleting container [{0}]...", inputContainerName);
				CloudBlobContainer container = blobClient.GetContainerReference(inputContainerName);
				await container.DeleteIfExistsAsync();
				
				// Clean up the job (if the user so chooses)
				Console.WriteLine();
				Console.Write("Delete job? [yes] no: ");
				string response = Console.ReadLine().ToLower();
				if (response != "n" && response != "no")
				{
					Console.WriteLine("Deleting job ...");
					await batchClient.JobOperations.DeleteJobAsync(JobId);
					Console.WriteLine("Job deleted.");
				}

				// Clean up the pool (if the user so chooses - do not delete the pool of new batches of videos are ready to process)
				Console.Write("Delete pool? [yes] no: ");
				response = Console.ReadLine().ToLower();
				if (response != "n" && response != "no")
				{
					Console.WriteLine("Deleting pool ...");
					await batchClient.PoolOperations.DeletePoolAsync(PoolId);
					Console.WriteLine("Pool deleted.");
				}
			}
		}

		/// <summary>
		/// Creates a job in the specified pool.
		/// </summary>
		/// <param name = "batchClient">A BatchClient object.</param>/
		/// <param name = "jobId">ID of the job to create.</param>
		/// <param name = "poolId">ID of the CloudPool object in which to create the job.</param>
		private static async Task CreateJobAsync(BatchClient batchClient, string jobId, string poolId)
		{
			Console.WriteLine("Creating job [{0}]...", jobId);
			CloudJob job = batchClient.JobOperations.CreateJob();
			job.Id = jobId;
			job.PoolInformation = new PoolInformation{PoolId = poolId};
			await job.CommitAsync();
		}

		/// <summary>
		/// Monitors the specified tasks for completion and whether errors occurred.
		/// </summary>
		/// <param name = "batchClient">A BatchClient object.</param>
		/// <param name = "jobId">ID of the job containing the tasks to be monitored.</param>
		/// <param name = "timeout">The period of time to wait for the tasks to reach the completed state.</param>
		private static async Task<bool> MonitorTasksAsync(BatchClient batchClient, string jobId, TimeSpan timeout)
		{
			bool allTasksSuccessful = true;
			const string completeMessage = "All tasks reached state Completed.";
			const string incompleteMessage = "One or more tasks failed to reach the Completed state within the timeout period.";
			const string successMessage = "Success! All tasks completed successfully. Output files uploaded to output container.";
			const string failureMessage = "One or more tasks failed.";
			Console.WriteLine("Monitoring all tasks for 'Completed' state, timeout in {0}...", timeout.ToString());
			
			// We use a TaskStateMonitor to monitor the state of our tasks. In this case, we will wait for all tasks to
			// reach the Completed state.
			TaskStateMonitor taskStateMonitor = batchClient.Utilities.CreateTaskStateMonitor();
			IEnumerable<CloudTask> addedTasks = batchClient.JobOperations.ListTasks(JobId);
			try
			{
				await taskStateMonitor.WhenAll(addedTasks, TaskState.Completed, timeout);
			}
			catch (TimeoutException)
			{
				await batchClient.JobOperations.TerminateJobAsync(jobId);
				Console.WriteLine(incompleteMessage);
				return false;
			}

			await batchClient.JobOperations.TerminateJobAsync(jobId);
			Console.WriteLine(completeMessage);
			
			// All tasks have reached the "Completed" state, however, this does not guarantee all tasks completed successfully.
			// Here we further check for any tasks with an execution result of "Failure".
			// Obtain the collection of tasks currently managed by the job. 
			// Use a detail level to specify that only the "id" property of each task should be populated. 
			// See https://docs.microsoft.com/en-us/azure/batch/batch-efficient-list-queries
			ODATADetailLevel detail = new ODATADetailLevel(selectClause: "executionInfo");
			
			// Filter for tasks with 'Failure' result.
			detail.FilterClause = "executionInfo/result eq 'Failure'";
			List<CloudTask> failedTasks = await batchClient.JobOperations.ListTasks(jobId, detail).ToListAsync();
			if (failedTasks.Any())
			{
				allTasksSuccessful = false;
				Console.WriteLine(failureMessage);
			}
			else
			{
				Console.WriteLine(successMessage);
			}

			return allTasksSuccessful;
		}

		/// <summary>
		/// Creates tasks to process each of the specified input files, and submits them
		/// to the specified job for execution.
		/// </summary>
		/// <param name = "batchClient">A BatchClient object.</param>
		/// <param name = "jobId">ID of the job to which the tasks are added.</param>
		/// <param name = "inputFiles">A collection of ResourceFile objects representing the input file
		/// to be processed by the tasks executed on the compute nodes.</param>
		/// <param name = "outputContainerSasUrl">The shared access signature URL for the Azure 
		/// Storagecontainer that will hold the output files that the tasks create.</param>
		/// <returns>A collection of the submitted cloud tasks.</returns>
		private static async Task<List<CloudTask>> AddTasksAsync(BatchClient batchClient, string jobId, List<ResourceFile> inputFiles, string outputContainerSasUrl)
		{
			Console.WriteLine("Adding {0} tasks to job [{1}]...", inputFiles.Count, jobId);
			// Create a collection to hold the tasks added to the job
			List<CloudTask> tasks = new List<CloudTask>();
			for (int i = 0; i < inputFiles.Count; i++)
			{
				// Assign a task ID for each iteration
				string taskId = String.Format("Task{0}", i);
				
				// Define task command line to convert the video format from MP4 to animated GIF using ffmpeg.
				// Note that ffmpeg syntax specifies the format as the file extension of the input file
				// and the output file respectively. In this case inputs are MP4.
				string appPath = String.Format("%AZ_BATCH_APP_PACKAGE_{0}#{1}%", appPackageId, appPackageVersion);
				string inputMediaFile = inputFiles[i].FilePath;
				string outputMediaFile = String.Format("{0}{1}", System.IO.Path.GetFileNameWithoutExtension(inputMediaFile), ".gif");
				
				// This is the dos command built by using the ffmpeg application package, the paths from the input container
				string taskCommandLine = String.Format("cmd /c {0}\\ffmpeg-3.4-win64-static\\bin\\ffmpeg.exe -i {1} {2}", appPath, inputMediaFile, outputMediaFile);
				
				// Create a cloud task (with the task ID and command line) and add it to the task list
				CloudTask task = new CloudTask(taskId, taskCommandLine);
				task.ResourceFiles = new List<ResourceFile>{inputFiles[i]};
				
				// Task output file will be uploaded to the output container in Storage.
				List<OutputFile> outputFileList = new List<OutputFile>();
				OutputFileBlobContainerDestination outputContainer = new OutputFileBlobContainerDestination(outputContainerSasUrl);
				OutputFile outputFile = new OutputFile(outputMediaFile, new OutputFileDestination(outputContainer), new OutputFileUploadOptions(OutputFileUploadCondition.TaskSuccess));
				outputFileList.Add(outputFile);
				task.OutputFiles = outputFileList;
				tasks.Add(task);
			}

			// Call BatchClient.JobOperations.AddTask() to add the tasks as a collection rather than making a
			// separate call for each. Bulk task submission helps to ensure efficient underlying API
			// calls to the Batch service.
			await batchClient.JobOperations.AddTaskAsync(jobId, tasks);
			return tasks;
		}

		private static async Task CreateContainerIfNotExistAsync(CloudBlobClient blobClient, string containerName)
		{
			CloudBlobContainer container = blobClient.GetContainerReference(containerName);
			await container.CreateIfNotExistsAsync();
			Console.WriteLine("Creating container [{0}].", containerName);
		}

		private static async Task<List<ResourceFile>> UploadFilesToContainerAsync(CloudBlobClient blobClient, string inputContainerName, List<string> filePaths)
		{
			List<ResourceFile> resourceFiles = new List<ResourceFile>();
			foreach (string filePath in filePaths)
			{
				resourceFiles.Add(await UploadResourceFileToContainerAsync(blobClient, inputContainerName, filePath));
			}

			return resourceFiles;
		}

		private static async Task<ResourceFile> UploadResourceFileToContainerAsync(CloudBlobClient blobClient, string containerName, string filePath)
		{
			Console.WriteLine("Uploading file {0} to container [{1}]...", filePath, containerName);
			string blobName = Path.GetFileName(filePath);
			var fileStream = System.IO.File.OpenRead(filePath);
			CloudBlobContainer container = blobClient.GetContainerReference(containerName);
			CloudBlockBlob blobData = container.GetBlockBlobReference(blobName);
			await blobData.UploadFromFileAsync(filePath);
			
			// Set the expiry time and permissions for the blob shared access signature. In this case, no start time is specified,
			// so the shared access signature becomes valid immediately
			SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy{SharedAccessExpiryTime = DateTime.UtcNow.AddHours(2), Permissions = SharedAccessBlobPermissions.Read};
			
			// Construct the SAS URL for the blob
			string sasBlobToken = blobData.GetSharedAccessSignature(sasConstraints);
			string blobSasUri = String.Format("{0}{1}", blobData.Uri, sasBlobToken);
			return ResourceFile.FromUrl(blobSasUri, blobName);
		}

		private static string GetContainerSasUrl(CloudBlobClient blobClient, string containerName, SharedAccessBlobPermissions permissions)
		{
			// Set the expiry time and permissions for the container access signature. In this case, no start time is specified,
			// so the shared access signature becomes valid immediately. Expiration is in 2 hours.
			SharedAccessBlobPolicy sasConstraints = new SharedAccessBlobPolicy{SharedAccessExpiryTime = DateTime.UtcNow.AddHours(2), Permissions = permissions};
			
			// Generate the shared access signature on the container, setting the constraints directly on the signature
			CloudBlobContainer container = blobClient.GetContainerReference(containerName);
			string sasContainerToken = container.GetSharedAccessSignature(sasConstraints);
			
			// Return the URL string for the container, including the SAS token
			return String.Format("{0}{1}", container.Uri, sasContainerToken);
		}

		private static async Task CreateBatchPoolAsync(BatchClient batchClient, string poolId)
		{
			CloudPool pool = null;
			Console.WriteLine("Creating pool [{0}]...", poolId);
			
			// Create an image reference object to store the settings for the nodes to be added to the pool
			ImageReference imageReference = new ImageReference(publisher: "MicrosoftWindowsServer", offer: "WindowsServer", sku: "2012-R2-Datacenter-smalldisk", version: "latest");
			
			// Use the image reference to create a VirtualMachineConfiguration object
			VirtualMachineConfiguration virtualMachineConfiguration = new VirtualMachineConfiguration(imageReference: imageReference, nodeAgentSkuId: "batch.node.windows amd64");
			try
			{
				// Create an unbound pool. No pool is actually created in the Batch service until we call
				// CloudPool.CommitAsync(). This CloudPool instance is therefore considered "unbound," and we can
				// modify its properties.
				pool = batchClient.PoolOperations.CreatePool(poolId: poolId, targetDedicatedComputeNodes: DedicatedNodeCount, targetLowPriorityComputeNodes: LowPriorityNodeCount, virtualMachineSize: PoolVMSize, virtualMachineConfiguration: virtualMachineConfiguration);
				
				// Specify the application and version to install on the compute nodes
				pool.ApplicationPackageReferences = new List<ApplicationPackageReference>{new ApplicationPackageReference{ApplicationId = appPackageId, Version = appPackageVersion}};
				// Create the pool
				await pool.CommitAsync();
			}
			catch (BatchException be)
			{
				// Accept the specific error code PoolExists as that is expected if the pool already exists
				if (be.RequestInformation?.BatchError?.Code == BatchErrorCodeStrings.PoolExists)
				{
					Console.WriteLine("The pool [{0}] already existed when we tried to create it", poolId);
				}
				else
				{
					throw; // Any other exception is unexpected
				}
			}
		}
	}
}