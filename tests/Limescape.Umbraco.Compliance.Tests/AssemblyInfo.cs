using Limescape.Umbraco.Compliance.Tests.Infrastructure;

[assembly: AssemblyFixture(typeof(UmbracoTestSite))]

// All integration tests share one Umbraco site on one SQLite file. Umbraco's Management API takes a read lock on the
// content tree and then upgrades to a write lock within one request; with a write in a parallel test class (for example
// creating a document type and its cache rebuild) SQLite then deadlocks for good. Run tests one after the other; SQL Server has no such issue.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
