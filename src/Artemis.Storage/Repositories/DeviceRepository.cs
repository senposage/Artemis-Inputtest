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
        dbContext.UpdateRange(deviceEntities);
        dbContext.SaveChanges();
    }
}
