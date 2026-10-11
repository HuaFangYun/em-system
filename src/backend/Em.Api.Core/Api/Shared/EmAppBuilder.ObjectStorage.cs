using Em.Api.Core;
using Em.Shared;
using Em.Api.Core.Models;
using Em.Api.Core.ObjectStorage;
using Em.Api.Core.ObjectStorage.Volumes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Em.Api.Shared;

public partial class EmAppBuilder
{
   private bool _objectStorageAdded;

   /// <summary>
   /// Turns on object storage: the <see cref="IObjectStore"/> service for modules, the management actions of
   /// <see cref="IObjectStorageServices"/>, background maintenance and the public <c>/store</c> path for
   /// public-read buckets. Volumes, policies and buckets are kept in the database and managed from the
   /// Object Storage Manager screen; changes apply without a restart.
   /// </summary>
   /// <remarks>
   /// Requires <c>doc/sqlscript/mssql/tables/060-objectstore.sql</c> and the <c>views/vi_Store*.sql</c> files
   /// on the core database (SQL Server); startup fails without them. Assumes one API instance per database and
   /// volume set. Guide: <c>doc/engine/engine-object-storage.md</c>.
   /// </remarks>
   public void AddObjectStorage() {
      if (_objectStorageAdded) throw new InvalidOperationException("AddObjectStorage may only be called once.");
      _objectStorageAdded = true;
      AddDbContext<ObjectStorageDbContext>();
      Services.AddSingleton<IStoreVolumeProvider>(sp => new LocalFolderVolumeProvider(sp.GetRequiredService<IHostEnvironment>().ContentRootPath));
      Services.AddSingleton<ObjectStorageRuntime>();
      Services.AddScoped<ObjectStore>();
      Services.AddScoped<IObjectStore>(sp => sp.GetRequiredService<ObjectStore>());
      Services.AddScoped<ObjectStorageMaintenance>();
      AddService<IObjectStorageServices, ObjectStorageServices>();
      AddClaims(ClaimAction.Create<ObjectStorageServices>(IObjectStorageServices.ManagerClaim),
         ClaimAction.Create<ObjectStorageServices>(IObjectStorageServices.SettingsClaim));
      Services.AddHostedService<ObjectStorageBackground>();
      AddPublicEndpoint(ObjectStorageEndpoint.PublicRequestPath, ObjectStorageEndpoint.HandleAsync);
   }
}
