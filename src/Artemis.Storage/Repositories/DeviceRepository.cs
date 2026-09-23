using System;
using System.Collections.Generic;
using System.Linq;
using Artemis.Storage.Entities.Surface;
using Artemis.Storage.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Artemis.Storage.Repositories;

internal class DeviceRepository(Func<ArtemisDbContext> getContext) : IDeviceRepository
{
    public void Add(DeviceEntity deviceEntity)
    {
        using ArtemisDbContext dbContext = getContext();
        dbContext.Devices.Add(deviceEntity);
        dbContext.SaveChanges();
    }

    public void Remove(DeviceEntity deviceEntity)
    {
        using ArtemisDbContext dbContext = getContext();
        dbContext.Devices.Remove(deviceEntity);
        dbContext.SaveChanges();
    }

    public DeviceEntity? Get(string id)
    {
        using ArtemisDbContext dbContext = getContext();
        return dbContext.Devices.FirstOrDefault(d => d.Id == id);
    }

    public DeviceEntity? Rename(string oldId, string newId)
    {
        using ArtemisDbContext dbContext = getContext();
        if (dbContext.Devices.Any(d => d.Id == newId))
            return dbContext.Devices.First(d => d.Id == newId);

        int updated = dbContext.Devices
            .Where(d => d.Id == oldId)
            .ExecuteUpdate(setters => setters.SetProperty(d => d.Id, newId));
        return updated == 0 ? null : dbContext.Devices.First(d => d.Id == newId);
    }

    public List<DeviceEntity> GetAll()
    {
        using ArtemisDbContext dbContext = getContext();
        return dbContext.Devices.ToList();
    }
    
    public void Save(DeviceEntity deviceEntity)
    {
        using ArtemisDbContext dbContext = getContext();
        dbContext.Update(deviceEntity);
        dbContext.SaveChanges();
    }
    
    public void SaveRange(IEnumerable<DeviceEntity> deviceEntities)
    {
        using ArtemisDbContext dbContext = getContext();
        List<DeviceEntity> entities = deviceEntities.DistinctBy(entity => entity.Id).ToList();
        SaveExisting(dbContext, entities);
    }

    private static void SaveExisting(ArtemisDbContext dbContext, List<DeviceEntity> entities)
    {
        if (entities.Count == 0)
            return;

        HashSet<string> existingIds = dbContext.Devices
            .Where(device => entities.Select(entity => entity.Id).Contains(device.Id))
            .Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        List<DeviceEntity> existingEntities = entities.Where(entity => existingIds.Contains(entity.Id)).ToList();
        if (existingEntities.Count == 0)
            return;

        dbContext.UpdateRange(existingEntities);
        try
        {
            dbContext.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            // A missing-device removal or identity migration can win the race against
            // a UI save. Do not recreate the intentionally removed row; retry only
            // the entries that are still present in the database.
            dbContext.ChangeTracker.Clear();
            existingIds = dbContext.Devices
                .Where(device => existingEntities.Select(entity => entity.Id).Contains(device.Id))
                .Select(device => device.Id)
                .ToHashSet(StringComparer.Ordinal);
            existingEntities = existingEntities.Where(entity => existingIds.Contains(entity.Id)).ToList();
            if (existingEntities.Count == 0)
                return;

            dbContext.UpdateRange(existingEntities);
            dbContext.SaveChanges();
        }
    }
}
