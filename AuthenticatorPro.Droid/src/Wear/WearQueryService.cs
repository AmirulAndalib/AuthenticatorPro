// Copyright (C) 2022 jmh
// SPDX-License-Identifier: GPL-3.0-only

using Android.App;
using Android.Gms.Wearable;
using AuthenticatorPro.Droid.Shared.Query;
using AuthenticatorPro.Droid.Util;
using AuthenticatorPro.Shared.Persistence;
using AuthenticatorPro.Shared.View;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AuthenticatorPro.Droid.Wear
{
    [Service(Exported = true)]
    [IntentFilter(
        new[] { MessageApi.ActionMessageReceived },
        DataScheme = "wear",
        DataHost = "*"
    )]
    internal class WearQueryService : WearableListenerService
    {
        private const string GetSyncBundleCapability = "get_sync_bundle";
        private const string GetCustomIconCapability = "get_custom_icon";

        private readonly Database _database;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private readonly IAuthenticatorView _authenticatorView;
        private readonly IAuthenticatorCategoryRepository _authenticatorCategoryRepository;
        private readonly ICategoryRepository _categoryRepository;
        private readonly ICustomIconRepository _customIconRepository;

        public WearQueryService()
        {
            _database = new Database();

            using var container = Dependencies.GetChildContainer();
            container.Register(_database);
            Dependencies.RegisterRepositories(container);
            Dependencies.RegisterServices(container);
            Dependencies.RegisterViews(container);

            _authenticatorView = container.Resolve<IAuthenticatorView>();
            _categoryRepository = container.Resolve<ICategoryRepository>();
            _authenticatorCategoryRepository = container.Resolve<IAuthenticatorCategoryRepository>();
            _customIconRepository = container.Resolve<ICustomIconRepository>();
        }

        private async Task OpenDatabase()
        {
            var password = await SecureStorageWrapper.GetDatabasePassword();
            await _database.Open(password, Database.Origin.Wear);
        }

        private async Task CloseDatabase()
        {
            await _database.Close(Database.Origin.Wear);
        }

        private async Task UseDatabase(Func<Task> action)
        {
            await _lock.WaitAsync();

            try
            {
                await OpenDatabase();
                await action();
                await CloseDatabase();
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task GetSyncBundle(string nodeId)
        {
            await _authenticatorView.LoadFromPersistenceAsync();
            var auths = new List<WearAuthenticator>();

            var authCategories = await _authenticatorCategoryRepository.GetAllAsync();

            foreach (var auth in _authenticatorView)
            {
                var bindings = authCategories
                    .Where(c => c.AuthenticatorSecret == auth.Secret)
                    .Select(c => new WearAuthenticatorCategory(c.CategoryId, c.Ranking))
                    .ToList();

                var item = new WearAuthenticator(
                    auth.Type, auth.Secret, auth.Pin, auth.Icon, auth.Issuer, auth.Username, auth.Period, auth.Digits,
                    auth.Algorithm, auth.Ranking, auth.CopyCount, bindings);

                auths.Add(item);
            }

            var categories = (await _categoryRepository.GetAllAsync())
                .Select(c => new WearCategory(c.Id, c.Name, c.Ranking))
                .ToList();

            var customIconIds = (await _customIconRepository.GetAllAsync())
                .Select(i => i.Id)
                .ToList();

            var preferenceWrapper = new PreferenceWrapper(this);
            var preferences = new WearPreferences(
                preferenceWrapper.DefaultCategory, preferenceWrapper.SortMode, preferenceWrapper.CodeGroupSize);

            var bundle = new WearSyncBundle(auths, categories, customIconIds, preferences);

            var json = JsonConvert.SerializeObject(bundle);
            var data = Encoding.UTF8.GetBytes(json);

            await WearableClass.GetMessageClient(this).SendMessageAsync(nodeId, GetSyncBundleCapability, data);
        }

        private async Task GetCustomIcon(string customIconId, string nodeId)
        {
            var icon = await _customIconRepository.GetAsync(customIconId);

            var data = Array.Empty<byte>();

            if (icon != null)
            {
                var response = new WearCustomIcon(icon.Id, icon.Data);
                var json = JsonConvert.SerializeObject(response);
                data = Encoding.UTF8.GetBytes(json);
            }

            await WearableClass.GetMessageClient(this).SendMessageAsync(nodeId, GetCustomIconCapability, data);
        }

        public override async void OnMessageReceived(IMessageEvent messageEvent)
        {
#if DEBUG
            Logger.Info($"Wear message received: {messageEvent.Path}");
#endif

            switch (messageEvent.Path)
            {
                case GetSyncBundleCapability:
                    await UseDatabase(() => GetSyncBundle(messageEvent.SourceNodeId));
                    break;

                case GetCustomIconCapability:
                {
                    var id = Encoding.UTF8.GetString(messageEvent.GetData());
                    await UseDatabase(() => GetCustomIcon(id, messageEvent.SourceNodeId));
                    break;
                }
            }
        }
    }
}