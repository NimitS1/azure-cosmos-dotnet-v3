﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Documents
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Contain the methods responsible for
    /// - Opening the Rntbd Direct connection to the backend replica nodes and
    /// - Sending the <see cref="DocumentServiceRequest"/> to the backend replica nodes for processing.
    /// </summary>
    internal interface IStoreClient
    {
        /// <summary>
        /// Invokes the replicated resource client and returns the document service response from the backend replica nodes.
        /// </summary>
        /// <param name="request">A instance of <see cref="DocumentServiceRequest"/> holding the request payload.</param>
        /// <param name="retryPolicy">An instance of <see cref="IRetryPolicy"/> defining the retry policy.</param>
        /// <param name="prepareRequestAsyncDelegate">A callback delegate to be invoked at a later point of time.</param>
        /// <param name="cancellationToken">An Instance of the <see cref="CancellationToken"/>.</param>
        /// <returns>A task of <see cref="DocumentServiceResponse"/> containing the response body.</returns>
        Task<DocumentServiceResponse> ProcessMessageAsync(
            DocumentServiceRequest request,
            IRetryPolicy retryPolicy = null,
            Func<DocumentServiceRequest, Task> prepareRequestAsyncDelegate = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Establishes and Initializes the Rntbd Direct connection to the backend replica nodes for the given database name and container.
        /// </summary>
        /// <param name="addresses">A string containing the name of the database.</param>
        /// <param name="cancellationToken">An Instance of the <see cref="CancellationToken"/>.</param>
        Task OpenConnectionsAsync(
            IEnumerable<Uri> addresses,
            CancellationToken cancellationToken = default);
    }
}
