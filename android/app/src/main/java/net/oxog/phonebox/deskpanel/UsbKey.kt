package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.util.Base64
import java.security.SecureRandom

/**
 * The key this app sends on the USB connection. "adb reverse" opens 127.0.0.1:8765 on the phone to every app on it, so
 * the agent's USB port answers only requests that carry this key. The app makes it once and keeps it; the agent reads it
 * over adb from [UsbKeyProvider], which only the adb shell may read.
 * The agent writes its TLS certificate's fingerprint (the pin) the same way. USB requests are TLS to that certificate
 * only, so an app that took the phone's port 8765 before the tunnel was set up never gets the key.
 */
object UsbKey {
    private const val PREFS = "usb"
    private const val KEY = "key"
    private const val PIN = "pin"
    private val PIN_FORMAT = Regex("^[0-9a-f]{64}$")
    @Volatile private var cached: String? = null
    @Volatile private var cachedPin: String? = null

    /** The agent's certificate fingerprint, or null until the agent has written it over adb (USB then stays unused). */
    fun pin(context: Context): String? {
        cachedPin?.let { return it }
        val stored = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getString(PIN, null)?.takeIf { PIN_FORMAT.matches(it) }
        cachedPin = stored
        return stored
    }

    @SuppressLint("ApplySharedPref")
    fun setPin(context: Context, pin: String): Boolean {
        if (!PIN_FORMAT.matches(pin)) return false
        synchronized(this) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putString(PIN, pin).commit()
            cachedPin = pin
        }
        return true
    }

    // commit, not apply: the agent may read the key from a process that ends right after, and a key that was never
    // written would make the next start hand the agent a different one
    @SuppressLint("ApplySharedPref")
    fun get(context: Context): String {
        cached?.let { return it }
        synchronized(this) {
            cached?.let { return it }
            val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val key = prefs.getString(KEY, null)?.takeIf { it.length >= 32 } ?: run {
                val bytes = ByteArray(32).also { SecureRandom().nextBytes(it) }
                val made = Base64.encodeToString(bytes, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
                prefs.edit().putString(KEY, made).commit()
                made
            }
            cached = key
            return key
        }
    }
}

/**
 * Hands [UsbKey] to the computer: "adb shell content query --uri content://net.oxog.phonebox.deskpanel.usb/key", and
 * takes the agent's certificate fingerprint: "adb shell content insert --uri content://…usb/pin --bind fp:s:<sha256>".
 * The manifest guards both with android.permission.DUMP, which the adb shell holds and no installed app can get, so
 * other apps on the phone can neither read the key nor set the pin. (call() is not used: Android doesn't check the
 * provider's permissions for it.)
 */
class UsbKeyProvider : ContentProvider() {
    override fun onCreate(): Boolean = true

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor? {
        if (uri.path != "/key") return null
        val ctx = context ?: return null
        return MatrixCursor(arrayOf("key")).apply { addRow(arrayOf<Any>(UsbKey.get(ctx))) }
    }

    override fun getType(uri: Uri): String? = null
    override fun insert(uri: Uri, values: ContentValues?): Uri? {
        if (uri.path != "/pin") return null
        val ctx = context ?: return null
        val pin = values?.getAsString("fp") ?: return null
        return if (UsbKey.setPin(ctx, pin)) uri else null
    }
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?): Int = 0
}
