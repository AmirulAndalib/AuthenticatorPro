﻿using Android.OS;
using AndroidX.Preference;

namespace AuthenticatorPro.Fragments
{
    internal class SettingsFragment : PreferenceFragmentCompat
    {
        public override void OnCreatePreferences(Bundle savedInstanceState, string rootKey)
        {
            AddPreferencesFromResource(Resource.Xml.settings);
        }
    }
}