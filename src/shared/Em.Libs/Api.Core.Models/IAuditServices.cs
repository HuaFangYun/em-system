using Em.Shared;

namespace Em.Api.Core.Models;

/// <summary>
/// Audit activity for module screens: the history of one document, all documents of a group, or one target.
/// Each action checks the read rule of the audit group (<c>CanRead</c> in <c>AddAudit</c>), not a Log
/// Manager claim. Pages use an opaque cursor; pass <see cref="AuditActivityPage.NextCursor"/> back as it is.
/// </summary>
public interface IAuditServices : IServices
{
   /// <summary>History of one document by its key.</summary>
   /// <param name="group">Audit group, e.g. <c>Invoice</c>.</param>
   /// <param name="key">Target key (EF primary key; composite keys as an ordered JSON array).</param>
   /// <param name="cursor">Cursor of the previous page, or <c>null</c>.</param>
   /// <param name="take">Page size, 1-200.</param>
   Task<AuditActivityPage> GetMeta_AuditActivity_ByDoc(string group, string key, string? cursor, int take);

   /// <summary>History of one document by its document number (409 when the number is used by several keys).</summary>
   Task<AuditActivityPage> GetMeta_AuditActivity_ByDocNo(string group, string docNo, string? cursor, int take);

   /// <summary>Activity of every document of a group.</summary>
   /// <param name="group">Audit group.</param>
   /// <param name="from">Start (UTC), optional.</param>
   /// <param name="to">End (UTC), optional.</param>
   /// <param name="userId">Only this actor.</param>
   /// <param name="kinds">Comma-separated kinds, optional.</param>
   /// <param name="cursor">Cursor of the previous page.</param>
   /// <param name="take">Page size, 1-200.</param>
   Task<AuditActivityPage> GetMeta_AuditActivity_ByType(string group, DateTime? from, DateTime? to, string? userId,
      string? kinds, string? cursor, int take);

   /// <summary>
   /// Activity about one target across several groups in one page, e.g. a user's account history
   /// (<c>System.User,System.Auth,System.Password</c>). Groups the caller may not read are left out.
   /// </summary>
   /// <param name="targetType">Target type, e.g. <c>user</c>.</param>
   /// <param name="key">Target key.</param>
   /// <param name="groups">Comma-separated groups.</param>
   /// <param name="cursor">Cursor of the previous page.</param>
   /// <param name="take">Page size, 1-200.</param>
   Task<AuditActivityPage> GetMeta_AuditActivity_ByTarget(string targetType, string key, string groups, string? cursor,
      int take);

   /// <summary>Entries newer than the given entry (refresh without reloading); <c>key</c> null = whole group.</summary>
   Task<AuditActivity[]> GetMeta_AuditActivity_Since(string group, string? key, DateTime afterTime, string? afterId);

   /// <summary>
   /// Records an event that only happens on the client (local print, export). The kind must be registered with
   /// <c>fromClient</c>; the server fills actor, time and address and marks the entry <c>source = client</c>.
   /// </summary>
   Task PostMeta_AuditEntry(string group, string key, string kind, string? docNo, string? detail);
}
