﻿/*
 *
 * (c) Copyright Ascensio System Limited 2010-2016
 *
 * This program is freeware. You can redistribute it and/or modify it under the terms of the GNU 
 * General Public License (GPL) version 3 as published by the Free Software Foundation (https://www.gnu.org/copyleft/gpl.html). 
 * In accordance with Section 7(a) of the GNU GPL its Section 15 shall be amended to the effect that 
 * Ascensio System SIA expressly excludes the warranty of non-infringement of any third-party rights.
 *
 * THIS PROGRAM IS DISTRIBUTED WITHOUT ANY WARRANTY; WITHOUT EVEN THE IMPLIED WARRANTY OF MERCHANTABILITY OR
 * FITNESS FOR A PARTICULAR PURPOSE. For more details, see GNU GPL at https://www.gnu.org/copyleft/gpl.html
 *
 * You can contact Ascensio System SIA by email at sales@onlyoffice.com
 *
 * The interactive user interfaces in modified source and object code versions of ONLYOFFICE must display 
 * Appropriate Legal Notices, as required under Section 5 of the GNU GPL version 3.
 *
 * Pursuant to Section 7 § 3(b) of the GNU GPL you must retain the original ONLYOFFICE logo which contains 
 * relevant author attributions when distributing the software. If the display of the logo in its graphic 
 * form is not reasonably feasible for technical reasons, you must include the words "Powered by ONLYOFFICE" 
 * in every copy of the program you distribute. 
 * Pursuant to Section 7 § 3(e) we decline to grant you any rights under trademark law for use of our trademarks.
 *
*/


using System;
using System.Collections.Generic;
using System.Linq;
using ASC.ActiveDirectory.Base.Settings;
using ASC.ActiveDirectory.ComplexOperations.Data;
using ASC.ActiveDirectory.Novell.Exceptions;
using ASC.Core;
using ASC.Core.Tenants;
using ASC.Core.Users;
using Newtonsoft.Json;
// ReSharper disable RedundantToStringCall

namespace ASC.ActiveDirectory.ComplexOperations
{
    public class LdapSaveSyncOperation : LdapOperation
    {
        private readonly LdapChangeCollection _ldapChanges;

        public LdapSaveSyncOperation(LdapSettings settings, Tenant tenant, LdapOperationType operation, LdapLocalization resource = null)
            : base(settings, tenant, operation, resource)
        {
            _ldapChanges = new LdapChangeCollection {Tenant = tenant};
        }

        protected override void Do()
        {
            try
            {
                if (OperationType == LdapOperationType.Save)
                {
                    SetProgress(10, Resource.LdapSettingsStatusSavingSettings);

                    LDAPSettings.IsDefault = LDAPSettings.Equals(LDAPSettings.GetDefault());

                    if (!LDAPSettings.Save())
                    {
                        Logger.Error("Can't save LDAP settings.");
                        Error = Resource.LdapSettingsErrorCantSaveLdapSettings;
                        return;
                    }
                }

                if (LDAPSettings.EnableLdapAuthentication)
                {
                    Logger.Debug("SyncLDAP()");

                    SyncLDAP();

                    if (!string.IsNullOrEmpty(Error))
                        return;
                }
                else
                {
                    Logger.Debug("TurnOffLDAP()");

                    TurnOffLDAP();
                }
            }
            catch (NovellLdapTlsCertificateRequestedException ex)
            {
                Logger.ErrorFormat(
                    "CheckSettings(acceptCertificate={0}, cert thumbprint: {1}): NovellLdapTlsCertificateRequestedException: {2}",
                    LDAPSettings.AcceptCertificate, LDAPSettings.AcceptCertificateHash, ex.ToString());
                Error = Resource.LdapSettingsStatusCertificateVerification;

                //TaskInfo.SetProperty(CERT_REQUEST, ex.CertificateConfirmRequest);
            }
            catch (TenantQuotaException e)
            {
                Logger.ErrorFormat("TenantQuotaException. {0}", e.ToString());
                Error = Resource.LdapSettingsTenantQuotaSettled;
            }
            catch (FormatException e)
            {
                Logger.ErrorFormat("FormatException error. {0}", e.ToString());
                Error = Resource.LdapSettingsErrorCantCreateUsers;
            }
            catch (Exception e)
            {
                Logger.ErrorFormat("Internal server error. {0}", e.ToString());
                Error = Resource.LdapSettingsInternalServerError;
            }
            finally
            {
                SetProgress(99, Resource.LdapSettingsStatusDisconnecting, "");
                Dispose();
            }

            SetProgress(100, OperationType == LdapOperationType.SaveTest ||
                             OperationType == LdapOperationType.SyncTest
                ? JsonConvert.SerializeObject(_ldapChanges)
                : "", "");
        }

        private void TurnOffLDAP()
        {
            const double percents = 48;

            SetProgress((int) percents, Resource.LdapSettingsModifyLdapUsers);

            var existingLDAPUsers = CoreContext.UserManager.GetUsers(EmployeeStatus.All).Where(u => u.Sid != null).ToList();

            var step = percents/existingLDAPUsers.Count;

            var percentage = (double) GetProgress();

            var index = 0;
            var count = existingLDAPUsers.Count;

            foreach (var existingLDAPUser in existingLDAPUsers)
            {
                SetProgress(Convert.ToInt32(percentage),
                    currentSource:
                        string.Format("({0}/{1}): {2}", ++index, count,
                            UserFormatter.GetUserName(existingLDAPUser, DisplayUserNameFormat.Default)));

                switch (OperationType)
                {
                    case LdapOperationType.Save:
                    case LdapOperationType.Sync:
                        existingLDAPUser.Sid = null;
                        existingLDAPUser.ConvertExternalContactsToOrdinary();

                        Logger.DebugFormat("CoreContext.UserManager.SaveUserInfo({0})", existingLDAPUser.GetUserInfoString());

                        CoreContext.UserManager.SaveUserInfo(existingLDAPUser);
                        break;
                    case LdapOperationType.SaveTest:
                    case LdapOperationType.SyncTest:
                        _ldapChanges.SetSaveAsPortalUserChange(existingLDAPUser);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                percentage += step;
            }
        }

        private void SyncLDAP()
        {
            if (!LDAPSettings.GroupMembership)
            {
                Logger.Debug("SyncLDAPUsers()");

                SyncLDAPUsers();
            }
            else
            {
                Logger.Debug("SyncLDAPUsersInGroups()");

                SyncLDAPUsersInGroups();
            }
        }

        private void SyncLDAPUsers()
        {
            SetProgress(15, Resource.LdapSettingsStatusGettingUsersFromLdap);

            var ldapUsers = Importer.GetDiscoveredUsersByAttributes();

            if (!ldapUsers.Any())
            {
                Error = Resource.LdapSettingsErrorUsersNotFound;
                return;
            }

            Logger.DebugFormat("Importer.GetDiscoveredUsersByAttributes() Success: Users count: {0}",
                Importer.AllDomainUsers.Count);

            SetProgress(20, Resource.LdapSettingsStatusRemovingOldUsers, "");

            ldapUsers = RemoveOldDbUsers(ldapUsers);

            SetProgress(30,
                OperationType == LdapOperationType.Save || OperationType == LdapOperationType.SaveTest
                    ? Resource.LdapSettingsStatusSavingUsers
                    : Resource.LdapSettingsStatusSyncingUsers,
                "");

            SyncDbUsers(ldapUsers);

            SetProgress(70, Resource.LdapSettingsStatusRemovingOldGroups, "");

            RemoveOldDbGroups(new List<GroupInfo>()); // Remove all db groups with sid
        }

        private void SyncLDAPUsersInGroups()
        {
            SetProgress(15, Resource.LdapSettingsStatusGettingGroupsFromLdap);

            var ldapGroups = Importer.GetDiscoveredGroupsByAttributes();

            if (!ldapGroups.Any())
            {
                Error = Resource.LdapSettingsErrorGroupsNotFound;
                return;
            }

            Logger.DebugFormat("Importer.GetDiscoveredGroupsByAttributes() Success: Groups count: {0}",
                Importer.AllDomainGroups.Count);

            SetProgress(20, Resource.LdapSettingsStatusGettingUsersFromLdap);

            //Get All found groups users
            List<UserInfo> uniqueLdapGroupUsers;

            var ldapGroupsUsers = GetGroupsUsers(ldapGroups, out uniqueLdapGroupUsers);

            if (!uniqueLdapGroupUsers.Any())
            {
                Error = Resource.LdapSettingsErrorUsersNotFound;
                return;
            }

            Logger.DebugFormat("GetGroupsUsers() Success: Users count: {0}",
                Importer.AllDomainUsers.Count);

            SetProgress(30,
                OperationType == LdapOperationType.Save || OperationType == LdapOperationType.SaveTest
                    ? Resource.LdapSettingsStatusSavingUsers
                    : Resource.LdapSettingsStatusSyncingUsers,
                "");

            var newUniqueLdapGroupUsers = SyncGroupsUsers(uniqueLdapGroupUsers);

            SetProgress(60, Resource.LdapSettingsStatusSavingGroups, "");

            SyncDbGroups(ldapGroupsUsers);

            SetProgress(80, Resource.LdapSettingsStatusRemovingOldGroups, "");

            RemoveOldDbGroups(ldapGroups);

            SetProgress(90, Resource.LdapSettingsStatusRemovingOldUsers, "");

            RemoveOldDbUsers(newUniqueLdapGroupUsers);
        }

        private void SyncDbGroups(Dictionary<GroupInfo, List<UserInfo>> ldapGroupsWithUsers)
        {
            const double percents = 20;

            var step = percents/ldapGroupsWithUsers.Count;

            var percentage = (double) GetProgress();

            if (!ldapGroupsWithUsers.Any())
                return;

            var gIndex = 0;
            var gCount = ldapGroupsWithUsers.Count;

            foreach (var ldapGroupWithUsers in ldapGroupsWithUsers)
            {
                var ldapGroup = ldapGroupWithUsers.Key;

                var ldapGroupUsers = ldapGroupWithUsers.Value;

                ++gIndex;

                SetProgress(Convert.ToInt32(percentage),
                    currentSource:
                        string.Format("({0}/{1}): {2}", gIndex,
                            gCount, ldapGroup.Name));

                var dbLdapGroup = CoreContext.UserManager.GetGroupInfoBySid(ldapGroup.Sid);

                if (Equals(dbLdapGroup, Constants.LostGroupInfo))
                {
                    AddNewGroup(ldapGroup, ldapGroupUsers, gIndex, gCount);
                }
                else
                {
                    UpdateDbGroup(dbLdapGroup, ldapGroup, ldapGroupUsers, gIndex, gCount);
                }

                percentage += step;
            }
        }

        private void AddNewGroup(GroupInfo ldapGroup, List<UserInfo> ldapGroupUsers, int gIndex, int gCount)
        {
            if (!ldapGroupUsers.Any()) // Skip empty groups
            {
                if (OperationType == LdapOperationType.SaveTest ||
                    OperationType == LdapOperationType.SyncTest)
                {
                    _ldapChanges.SetSkipGroupChange(ldapGroup);
                }

                return;
            }

            var groupMembersToAdd =
                ldapGroupUsers.Select(ldapGroupUser => SearchDbUserBySid(ldapGroupUser.Sid))
                    .Where(userBySid => !Equals(userBySid, Constants.LostUser))
                    .ToList();

            if (groupMembersToAdd.Any())
            {
                switch (OperationType)
                {
                    case LdapOperationType.Save:
                    case LdapOperationType.Sync:
                        ldapGroup = CoreContext.UserManager.SaveGroupInfo(ldapGroup);

                        var index = 0;
                        var count = groupMembersToAdd.Count;

                        foreach (var userBySid in groupMembersToAdd)
                        {
                            SetProgress(
                                currentSource:
                                    string.Format("({0}/{1}): {2}, {3} ({4}/{5}): {6}", gIndex,
                                        gCount, ldapGroup.Name,
                                        Resource.LdapSettingsStatusAddingGroupUser,
                                        ++index, count,
                                        UserFormatter.GetUserName(userBySid, DisplayUserNameFormat.Default)));

                            CoreContext.UserManager.AddUserIntoGroup(userBySid.ID, ldapGroup.ID);
                        }
                        break;
                    case LdapOperationType.SaveTest:
                    case LdapOperationType.SyncTest:
                        _ldapChanges.SetAddGroupChange(ldapGroup);
                        _ldapChanges.SetAddGroupMembersChange(ldapGroup, groupMembersToAdd);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            else
            {
                if (OperationType == LdapOperationType.SaveTest ||
                    OperationType == LdapOperationType.SyncTest)
                {
                    _ldapChanges.SetSkipGroupChange(ldapGroup);
                }
            }
        }

        private static bool NeedUpdateGroup(GroupInfo portalGroup, GroupInfo ldapGroup)
        {
            var needUpdate =
                !portalGroup.Name.Equals(ldapGroup.Name, StringComparison.InvariantCultureIgnoreCase) ||
                !portalGroup.Sid.Equals(ldapGroup.Sid, StringComparison.InvariantCultureIgnoreCase);

            return needUpdate;
        }

        private void UpdateDbGroup(GroupInfo dbLdapGroup, GroupInfo ldapGroup, List<UserInfo> ldapGroupUsers, int gIndex,
            int gCount)
        {
            SetProgress(currentSource:
                string.Format("({0}/{1}): {2}", gIndex, gCount, ldapGroup.Name));

            var dbGroupMembers =
                        CoreContext.UserManager.GetUsersByGroup(dbLdapGroup.ID, EmployeeStatus.All)
                            .Where(u => u.Sid != null)
                            .ToList();

            var groupMembersToRemove =
                dbGroupMembers.Where(
                    dbUser => ldapGroupUsers.FirstOrDefault(lu => dbUser.Sid.Equals(lu.Sid)) == null).ToList();

            var groupMembersToAdd = (from ldapGroupUser in ldapGroupUsers
                let dbUser = dbGroupMembers.FirstOrDefault(u => u.Sid.Equals(ldapGroupUser.Sid))
                where dbUser == null
                select SearchDbUserBySid(ldapGroupUser.Sid)
                into userBySid
                where !Equals(userBySid, Constants.LostUser)
                select userBySid)
                .ToList();

            switch (OperationType)
            {
                case LdapOperationType.Save:
                case LdapOperationType.Sync:
                    if (NeedUpdateGroup(dbLdapGroup, ldapGroup))
                    {
                        dbLdapGroup.Name = ldapGroup.Name;
                        dbLdapGroup.Sid = ldapGroup.Sid;

                        dbLdapGroup = CoreContext.UserManager.SaveGroupInfo(dbLdapGroup);
                    }

                    var index = 0;
                    var count = groupMembersToRemove.Count;

                    foreach (var dbUser in groupMembersToRemove)
                    {
                        SetProgress(
                            currentSource:
                                string.Format("({0}/{1}): {2}, {3} ({4}/{5}): {6}", gIndex, gCount,
                                    dbLdapGroup.Name,
                                    Resource.LdapSettingsStatusRemovingGroupUser,
                                    ++index, count,
                                    UserFormatter.GetUserName(dbUser, DisplayUserNameFormat.Default)));

                        CoreContext.UserManager.RemoveUserFromGroup(dbUser.ID, dbLdapGroup.ID);
                    }

                    index = 0;
                    count = groupMembersToAdd.Count;

                    foreach (var userInfo in groupMembersToAdd)
                    {
                        SetProgress(
                            currentSource:
                                string.Format("({0}/{1}): {2}, {3} ({4}/{5}): {6}", gIndex, gCount,
                                    ldapGroup.Name,
                                    Resource.LdapSettingsStatusAddingGroupUser,
                                    ++index, count,
                                    UserFormatter.GetUserName(userInfo, DisplayUserNameFormat.Default)));

                        CoreContext.UserManager.AddUserIntoGroup(userInfo.ID, dbLdapGroup.ID);
                    }

                    if (dbGroupMembers.All(dbUser => groupMembersToRemove.Exists(u => u.ID.Equals(dbUser.ID)))
                        && !groupMembersToAdd.Any())
                    {
                        SetProgress(currentSource:
                            string.Format("({0}/{1}): {2}", gIndex, gCount, dbLdapGroup.Name));

                        CoreContext.UserManager.DeleteGroup(dbLdapGroup.ID);
                    }

                    break;
                case LdapOperationType.SaveTest:
                case LdapOperationType.SyncTest:
                    if (NeedUpdateGroup(dbLdapGroup, ldapGroup))
                        _ldapChanges.SetUpdateGroupChange(ldapGroup);

                    if (groupMembersToRemove.Any())
                        _ldapChanges.SetRemoveGroupMembersChange(dbLdapGroup, groupMembersToRemove);

                    if (groupMembersToAdd.Any())
                        _ldapChanges.SetAddGroupMembersChange(dbLdapGroup, groupMembersToAdd);

                    if (dbGroupMembers.All(dbUser => groupMembersToRemove.Exists(u => u.ID.Equals(dbUser.ID)))
                        && !groupMembersToAdd.Any())
                    {
                        _ldapChanges.SetRemoveGroupChange(dbLdapGroup, Logger);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static UserInfo SearchDbUserBySid(string sid)
        {
            if (string.IsNullOrEmpty(sid))
                return Constants.LostUser;

            var foundUser = CoreContext.UserManager.GetUserBySid(sid);

            return foundUser;
        }

        private void SyncDbUsers(List<UserInfo> ldapUsers)
        {
            const double percents = 35;

            var step = percents/ldapUsers.Count;

            var percentage = (double) GetProgress();

            if (!ldapUsers.Any())
                return;

            var index = 0;
            var count = ldapUsers.Count;

            foreach (var userInfo in ldapUsers)
            {
                SetProgress(Convert.ToInt32(percentage),
                    currentSource:
                        string.Format("({0}/{1}): {2}", ++index, count,
                            UserFormatter.GetUserName(userInfo, DisplayUserNameFormat.Default)));

                switch (OperationType)
                {
                    case LdapOperationType.Save:
                    case LdapOperationType.Sync:
                        LDAPUserManager.SyncLDAPUser(userInfo, ldapUsers);
                        break;
                    case LdapOperationType.SaveTest:
                    case LdapOperationType.SyncTest:
                        LdapChangeCollection changes;
                        LDAPUserManager.GetLDAPSyncUserChange(userInfo, ldapUsers, out changes);
                        _ldapChanges.AddRange(changes);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                percentage += step;
            }
        }

        /// <summary>
        /// Remove old LDAP users from db
        /// </summary>
        /// <param name="ldapUsers">list of actual LDAP users</param>
        /// <returns>New list of actual LDAP users</returns>
        private List<UserInfo> RemoveOldDbUsers(List<UserInfo> ldapUsers)
        {
            var dbLdapUsers = CoreContext.UserManager.GetUsers(EmployeeStatus.All).Where(u => u.Sid != null).ToList();

            if (!dbLdapUsers.Any())
                return ldapUsers;

            var removedUsers =
                dbLdapUsers.Where(u => ldapUsers.FirstOrDefault(lu => u.Sid.Equals(lu.Sid)) == null).ToList();

            if (!removedUsers.Any())
                return ldapUsers;

            const double percents = 8;

            var step = percents/removedUsers.Count;

            var percentage = (double) GetProgress();

            var index = 0;
            var count = removedUsers.Count;

            foreach (var removedUser in removedUsers)
            {
                SetProgress(Convert.ToInt32(percentage),
                    currentSource:
                        string.Format("({0}/{1}): {2}", ++index, count,
                            UserFormatter.GetUserName(removedUser, DisplayUserNameFormat.Default)));

                switch (OperationType)
                {
                    case LdapOperationType.Save:
                    case LdapOperationType.Sync:
                        removedUser.Sid = null;
                        if (!removedUser.IsOwner())
                            removedUser.Status = EmployeeStatus.Terminated; // Disable user on portal

                        removedUser.ConvertExternalContactsToOrdinary();

                        Logger.DebugFormat("CoreContext.UserManager.SaveUserInfo({0})", removedUser.GetUserInfoString());

                        CoreContext.UserManager.SaveUserInfo(removedUser);
                        break;
                    case LdapOperationType.SaveTest:
                    case LdapOperationType.SyncTest:
                        _ldapChanges.SetSaveAsPortalUserChange(removedUser);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                percentage += step;
            }

            dbLdapUsers.RemoveAll(removedUsers.Contains);

            var newLdapUsers = ldapUsers.Where(u => !removedUsers.Exists(ru => ru.ID.Equals(u.ID))).ToList();

            return newLdapUsers;
        }

        private void RemoveOldDbGroups(List<GroupInfo> ldapGroups)
        {
            var percentage = (double) GetProgress();

            var removedDbLdapGroups =
                CoreContext.UserManager.GetGroups()
                    .Where(g => g.Sid != null && ldapGroups.FirstOrDefault(lg => g.Sid.Equals(lg.Sid)) == null)
                    .ToList();

            if (!removedDbLdapGroups.Any())
                return;

            const double percents = 10;

            var step = percents/removedDbLdapGroups.Count;

            var index = 0;
            var count = removedDbLdapGroups.Count;

            foreach (var groupInfo in removedDbLdapGroups)
            {
                SetProgress(Convert.ToInt32(percentage),
                    currentSource: string.Format("({0}/{1}): {2}", ++index, count, groupInfo.Name));

                switch (OperationType)
                {
                    case LdapOperationType.Save:
                    case LdapOperationType.Sync:
                        CoreContext.UserManager.DeleteGroup(groupInfo.ID);
                        break;
                    case LdapOperationType.SaveTest:
                    case LdapOperationType.SyncTest:
                        _ldapChanges.SetRemoveGroupChange(groupInfo);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                percentage += step;
            }
        }

        private List<UserInfo> SyncGroupsUsers(List<UserInfo> uniqueLdapGroupUsers)
        {
            const double percents = 30;

            var step = percents/uniqueLdapGroupUsers.Count;

            var percentage = (double) GetProgress();

            var newUniqueLdapGroupUsers = new List<UserInfo>();

            var index = 0;
            var count = uniqueLdapGroupUsers.Count;

            int i, len;
            for (i = 0, len = uniqueLdapGroupUsers.Count; i < len; i++)
            {
                var ldapGroupUser = uniqueLdapGroupUsers[i];

                SetProgress(Convert.ToInt32(percentage),
                    currentSource:
                        string.Format("({0}/{1}): {2}", ++index, count,
                            UserFormatter.GetUserName(ldapGroupUser, DisplayUserNameFormat.Default)));

                UserInfo user;
                switch (OperationType)
                {
                    case LdapOperationType.Save:
                    case LdapOperationType.Sync:
                        user = LDAPUserManager.SyncLDAPUser(ldapGroupUser, uniqueLdapGroupUsers);
                        if (!Equals(user, Constants.LostUser))
                            newUniqueLdapGroupUsers.Add(user);
                        break;
                    case LdapOperationType.SaveTest:
                    case LdapOperationType.SyncTest:
                        LdapChangeCollection changes;
                        user = LDAPUserManager.GetLDAPSyncUserChange(ldapGroupUser, uniqueLdapGroupUsers, out changes);
                        if (!Equals(user, Constants.LostUser))
                        {
                            newUniqueLdapGroupUsers.Add(user);
                        }
                        _ldapChanges.AddRange(changes);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                percentage += step;
            }

            return newUniqueLdapGroupUsers;
        }

        private Dictionary<GroupInfo, List<UserInfo>> GetGroupsUsers(List<GroupInfo> ldapGroups,
            out List<UserInfo> uniqueLdapGroupUsers)
        {
            uniqueLdapGroupUsers = new List<UserInfo>();

            var listGroupsUsers = new Dictionary<GroupInfo, List<UserInfo>>();

            foreach (var ldapGroup in ldapGroups)
            {
                var ldapGroupUsers = Importer.GetGroupUsers(ldapGroup);

                listGroupsUsers.Add(ldapGroup, ldapGroupUsers);

                foreach (var ldapGroupUser in ldapGroupUsers)
                {
                    if (!uniqueLdapGroupUsers.Any(u => u.Sid.Equals(ldapGroupUser.Sid)))
                    {
                        uniqueLdapGroupUsers.Add(ldapGroupUser);
                    }
                }
            }

            return listGroupsUsers;
        }
    }
}