﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using ChanSort.Api;

namespace ChanSort.Loader.Toshiba
{
  /*
   * This class loads Toshiba files stores as CLONE00001\settingsDB.db along with settingsDBBackup.db
   * Currently only channel renaming, reordering and deletion is supported.
   * We don't know yet how/where information about favorites and skip/lock/hide is stored.
   *
   * Also, there are SatTable and SatTxTable for satellites and transponders in the file, but it's unknown
   * how these tables are linked with EASISerTable / DVBSerTable / ...
   */
  class SettingsDbSerializer : SerializerBase
  {
    private readonly ChannelList channels = new ChannelList(SignalSource.All, "All");

    #region ctor()
    public SettingsDbSerializer(string inputFile) : base(inputFile)
    {
      DepencencyChecker.AssertVc2010RedistPackageX86Installed();      

      this.Features.ChannelNameEdit = ChannelNameEditMode.All;
      this.Features.DeleteMode = DeleteMode.Physically;
      this.Features.CanSkipChannels = false;
      this.Features.CanLockChannels = false;
      this.Features.CanHideChannels = false;
      this.Features.SupportedFavorites = 0;

      this.DataRoot.AddChannelList(this.channels);
    }
    #endregion

    #region GetDataFilePaths()
    public override IEnumerable<string> GetDataFilePaths()
    {
      var list = new List<string>();
      list.Add(this.FileName);
      var backupFile = GetBackupFilePath();
      if (File.Exists(backupFile))
        list.Add(backupFile);
      return list;
    }

    private string GetBackupFilePath()
    {
      var dir = Path.GetDirectoryName(this.FileName);
      var name = Path.GetFileNameWithoutExtension(this.FileName);
      var ext = Path.GetExtension(this.FileName);
      var backupFile = Path.Combine(dir, name + "Backup" + ext);
      return backupFile;
    }

    #endregion

    #region Load()
    public override void Load()
    {
      string sysDataConnString = "Data Source=" + this.FileName;
      using var conn = new SQLiteConnection(sysDataConnString);
      conn.Open();
      using var cmd = conn.CreateCommand();

      this.RepairCorruptedDatabaseImage(cmd);

      cmd.CommandText = "SELECT count(1) FROM sqlite_master WHERE type = 'table' and name='EASISerTable'";
      if (Convert.ToInt32(cmd.ExecuteScalar()) != 1)
        throw new FileLoadException("File doesn't contain the expected tables");

      this.ReadSatellites(cmd);
      this.ReadTransponders(cmd);
      this.ReadChannels(cmd);
    }
    #endregion
    
    #region RepairCorruptedDatabaseImage()
    private void RepairCorruptedDatabaseImage(SQLiteCommand cmd)
    {
      cmd.CommandText = "REINDEX";
      cmd.ExecuteNonQuery();
    }
    #endregion

    #region ReadSatellites()
    private void ReadSatellites(SQLiteCommand cmd)
    {
      cmd.CommandText = "select m_id, m_name_serialized, m_orbital_position from SatTable";
      using var r = cmd.ExecuteReader();
      while (r.Read())
      {
        Satellite sat = new Satellite(r.GetInt32(0));
        string eastWest = "E";
        int pos = r.GetInt32(2);
        if (pos < 0)
        {
          pos = -pos;
          eastWest = "W";
        }
        sat.OrbitalPosition = $"{pos / 10}.{pos % 10}{eastWest}";
        sat.Name = r.GetString(1);
        this.DataRoot.AddSatellite(sat);
      }
    }
    #endregion

    #region ReadTransponders()
    private void ReadTransponders(SQLiteCommand cmd)
    {
      cmd.CommandText = "select m_id, m_satellite_id, m_frequency, m_polarisation, m_symbol_rate from SatTxTable";
      using var r = cmd.ExecuteReader();
      while (r.Read())
      {
        int id = r.GetInt32(0);
        int satId = r.GetInt32(1);
        int freq = r.GetInt32(2);
        
        if (this.DataRoot.Transponder.TryGet(id) != null)
          continue;
        Transponder tp = new Transponder(id);
        tp.FrequencyInMhz = (decimal)freq;
        tp.Polarity = r.GetInt32(3) == 0 ? 'H' : 'V';
        tp.Satellite = this.DataRoot.Satellites.TryGet(satId);
        tp.SymbolRate = r.GetInt32(4);
        this.DataRoot.AddTransponder(tp.Satellite, tp);
      }
    }
    #endregion

    #region ReadChannels()
    private void ReadChannels(SQLiteCommand cmd)
    {
      int ixE = 0;
      int ixD = 3;
      int ixA = 8;
      int ixT = 9;
      int ixDC = 10;

      cmd.CommandText = @"
select 
  e.m_handle, e.m_rsn, e.m_name_serialized, 
  d.m_onid, d.m_tsid, d.m_id, d.m_type, d.m_name_serialized,
  a.m_name_serialized,
  t.frequency_in_multiples_of_10Hz,
  dc.frequency
from EASISerTable e 
left outer join DVBSerTable d on d.m_handle=e.m_handle
left outer join AnalogSerTable a on a.m_handle=e.m_handle
left outer join ChanDataTable dc on dc.handle=d.m_channel_no
left outer join ChanDataTable ac on ac.handle=a.m_channel_no
left outer join TADTunerDataTable t on t.channel=ac.channel_no";
      using var r = cmd.ExecuteReader();
      while (r.Read())
      {
        var handle = r.GetInt32(ixE + 0);
        var oldProgNr = r.GetInt32(ixE + 1);
        var name = r.GetString(ixE + 2);
        ChannelInfo channel = new ChannelInfo(0, handle, oldProgNr, name);

        // DVB
        if (!r.IsDBNull(ixD + 0))
        {
          channel.OriginalNetworkId = r.GetInt32(ixD + 0) & 0x1FFF;
          channel.TransportStreamId = r.GetInt32(ixD + 1) & 0x1FFF;
          channel.ServiceId = r.GetInt32(ixD + 2) & 0x1FFF;
          channel.ServiceType = r.GetInt32(ixD + 3);
          channel.FreqInMhz = (decimal) r.GetInt32(ixDC + 0) / 1000;
        }

        // analog
        if (!r.IsDBNull(ixA + 0))
        {
          channel.FreqInMhz = (decimal) r.GetInt32(ixT + 0) / 100000;
        }

        if (!channel.IsDeleted)
          this.DataRoot.AddChannel(this.channels, channel);
      }
    }
    #endregion


    #region Save()
    public override void Save(string tvOutputFile)
    {
      if (tvOutputFile != this.FileName)
      {
        File.Copy(this.FileName, tvOutputFile, true);
        this.FileName = tvOutputFile;
      }

      string channelConnString = "Data Source=" + this.FileName;
      using var conn = new SQLiteConnection(channelConnString);
      conn.Open();
      using var cmd = conn.CreateCommand();
      using var cmd2 = conn.CreateCommand();
      using var trans = conn.BeginTransaction();

      this.WriteChannels(cmd, cmd2, this.channels);
      trans.Commit();

      this.RepairCorruptedDatabaseImage(cmd);
      conn.Close();

      // copy settingsDB.db to settingsDBBackup.db
      var backupFile = GetBackupFilePath();
      File.Copy(this.FileName, backupFile, true);
    }
    #endregion

    #region WriteChannels()
    private void WriteChannels(SQLiteCommand cmd, SQLiteCommand cmdDelete, ChannelList channelList)
    {
      cmd.CommandText = "update EASISerTable set m_rsn=@nr, m_name_serialized=@name where m_handle=@handle";
      cmd.Parameters.Add(new SQLiteParameter("@handle", DbType.Int32));
      cmd.Parameters.Add(new SQLiteParameter("@nr", DbType.Int32));
      cmd.Parameters.Add(new SQLiteParameter("@name", DbType.String));
      cmd.Prepare();

      cmdDelete.CommandText = @"
delete from EASISerTable where m_handle=@handle; 
delete from DVBSerTable where m_handle=@handle; 
delete from AnalogSerTable where m_handle=@handle;
";
      cmdDelete.Parameters.Add(new SQLiteParameter("@handle", DbType.Int32));

      foreach (ChannelInfo channel in channelList.Channels)
      {
        if (channel.IsProxy) // ignore reference list proxy channels
          continue;

        if (channel.IsDeleted)
        {
          cmdDelete.Parameters["@handle"].Value = channel.RecordIndex;
          cmdDelete.ExecuteNonQuery();
        }
        else
        {
          channel.UpdateRawData();
          cmd.Parameters["@handle"].Value = channel.RecordIndex;
          cmd.Parameters["@nr"].Value = channel.NewProgramNr;
          cmd.Parameters["@name"].Value = channel.Name;
          cmd.ExecuteNonQuery();
        }
      }
    }
    #endregion
  }
}
