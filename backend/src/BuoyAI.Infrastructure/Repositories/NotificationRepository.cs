using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class NotificationRepository : INotificationRepository
{
    private readonly BuoyAIDbContext _context;

    public NotificationRepository(BuoyAIDbContext context)
    {
        _context = context;
    }

    public async Task<List<Notification>> GetUserNotificationsAsync(int userId, int page, int pageSize)
    {
        return await _context.Notifications
            .Where(n => n.UserId == userId)
            .Include(n => n.RelatedConnection)
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<Notification?> GetByIdAsync(int id)
    {
        return await _context.Notifications
            .Include(n => n.RelatedConnection)
            .FirstOrDefaultAsync(n => n.Id == id);
    }

    public async Task<Notification> CreateAsync(Notification notification)
    {
        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync();
        return notification;
    }

    public async Task<Notification> UpdateAsync(Notification notification)
    {
        notification.UpdatedAt = DateTime.UtcNow;
        _context.Notifications.Update(notification);
        await _context.SaveChangesAsync();
        return notification;
    }

    public async Task<int> GetUnreadCountAsync(int userId)
    {
        return await _context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .CountAsync();
    }

    public async Task<bool> MarkAllAsReadAsync(int userId)
    {
        var unreadNotifications = await _context.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ToListAsync();

        foreach (var notification in unreadNotifications)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            notification.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<List<Notification>> GetAllAsync()
    {
        return await _context.Notifications
            .Include(n => n.RelatedConnection)
            .ToListAsync();
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var notification = await _context.Notifications.FindAsync(id);
        if (notification == null)
        {
            return false;
        }

        _context.Notifications.Remove(notification);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<List<Notification>> GetUserConnectionNotificationsAsync(int userId, int connectionId, string[] notificationTypes)
    {
        return await _context.Notifications
            .Where(n => n.UserId == userId && 
                       n.RelatedConnectionId == connectionId && 
                       !n.IsRead && 
                       notificationTypes.Contains(n.Type))
            .Include(n => n.RelatedConnection)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();
    }

    public async Task<int> GetNotificationCountByTypeAsync(int userId, string type, DateTime since)
    {
        return await _context.Notifications
            .CountAsync(n => n.UserId == userId && 
                           n.Type == type && 
                           n.CreatedAt >= since);
    }
}
