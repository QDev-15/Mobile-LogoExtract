using Android.App;
using Android.Gms.Extensions;
using Xamarin.Google.Android.Play.Core.AppUpdate;
using Xamarin.Google.Android.Play.Core.AppUpdate.Install.Model;

namespace LogoExtract.Services;

/// <summary>
/// Forces every user onto the latest Play-published build: whenever Play reports a newer version exists,
/// blocks the app behind Play's own full-screen update UI (AppUpdateType.Immediate) until they update or
/// quit. No server of our own. Ported from DocScanner/PdfReader's AndroidAppUpdate (same two sibling repos
/// everything else in this csproj already matches); the only change is the log tag.
/// </summary>
internal static class AndroidAppUpdate
{
	public static async void CheckAndForce(Activity activity)
	{
		try
		{
			IAppUpdateManager manager = AppUpdateManagerFactory.Create(activity);
			AppUpdateInfo info = await manager.GetAppUpdateInfo().AsAsync<AppUpdateInfo>();
			int availability = info.UpdateAvailability();

			// Already underway (e.g. the user backed out mid-download last time): resume it, same call as
			// starting fresh - Play itself knows to continue rather than restart the download.
			bool resuming = availability == UpdateAvailability.DeveloperTriggeredUpdateInProgress;
			bool starting = availability == UpdateAvailability.UpdateAvailable && info.IsUpdateTypeAllowed(AppUpdateType.Immediate);
			if (!resuming && !starting) return;

			const int requestCode = 0x5047;
			manager.StartUpdateFlowForResult(info, activity,
				AppUpdateOptions.NewBuilder(AppUpdateType.Immediate).Build(), requestCode);
		}
		catch (Exception ex)
		{
			// No Play Services (a sideloaded install outside Play, or a device without it), or a transient
			// Play error: never let this block or crash the app - the user just does not get asked this
			// time; the next OnResume tries again.
			Android.Util.Log.Warn("LogoExtract", $"update check failed: {ex.Message}");
		}
	}
}
