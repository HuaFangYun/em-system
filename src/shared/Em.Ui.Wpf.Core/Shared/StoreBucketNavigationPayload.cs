using Em.Ui.Core.Shared;
using Em.Ui.Wpf.Core;

namespace Em.Ui.Wpf.Shared
{
   /// <summary>
   /// Navigation parameter of the object storage bucket editor (<see cref="EmApp.ObjectStorageBucketNavigationName"/>):
   /// the bucket to open by name, and optionally the folder (key prefix) to show first. Any screen can hand a
   /// bucket over to the editor with it, e.g.
   /// <c>NavigationEntry.NavigateTo(EmApp.ObjectStorageBucketNavigationName, StoreBucketNavigationPayload.Open("invoices", "2026/"))</c>.
   /// </summary>
   public sealed class StoreBucketNavigationPayload : NavigationPayloadBase
   {
      private StoreBucketNavigationPayload(string? bucketName, string? prefix) : base(bucketName) {
         Prefix = NormalizePrefix(prefix);
         DataState = bucketName is null ? DataState.NewData : DataState.EditData;
      }

      /// <summary>Opens an existing bucket by name, at its root or at <paramref name="prefix"/>.</summary>
      /// <param name="bucketName">Name of the bucket.</param>
      /// <param name="prefix">Folder to show first, e.g. <c>2026/</c>; a missing trailing <c>/</c> is added.</param>
      public static StoreBucketNavigationPayload Open(string bucketName, string? prefix = null) {
         ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
         return new StoreBucketNavigationPayload(bucketName.Trim(), prefix);
      }

      /// <summary>Opens the editor for a new bucket; its objects can be managed once it is saved.</summary>
      public static StoreBucketNavigationPayload New() => new(null, null);

      /// <summary>Name of the bucket; <c>null</c> for a new bucket.</summary>
      public string? BucketName => (string?)Data;

      /// <summary>Folder shown first: empty for the bucket root, otherwise ending with <c>/</c>.</summary>
      public string Prefix { get; }

      /// <summary>"New Bucket", or "Bucket: " followed by the bucket name, which is unique.</summary>
      public override string? Title => BucketName is { } name ? TitleOf(name) : "New Bucket";

      internal static string TitleOf(string bucketName) => $"Bucket: {bucketName}";

      internal static string NormalizePrefix(string? prefix) {
         var value = (prefix ?? "").TrimStart('/');
         return value.Length == 0 || value.EndsWith('/') ? value : value + "/";
      }
   }
}
