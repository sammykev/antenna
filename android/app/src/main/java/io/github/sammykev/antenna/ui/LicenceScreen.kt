package io.github.sammykev.antenna.ui

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.sammykev.antenna.licence.LicenceStatus
import io.github.sammykev.antenna.licence.Licensing
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

fun openSite(context: Context, url: String) = context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))

/** The paywall: shown instead of the app until the phone is signed in and the account has a trial or plan running. */
@Composable
fun LicenceScreen(status: LicenceStatus) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var code by remember { mutableStateOf<String?>(null) }
    var message by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var waiting by remember { mutableStateOf<Job?>(null) }

    fun signIn() {
        busy = true
        message = null
        waiting?.cancel()
        waiting = scope.launch {
            try {
                val start = Licensing.startSignIn(context)
                code = start.userCode
                busy = false
                openSite(context, Licensing.client.linkUrl(start.userCode))
                Licensing.waitForSignIn(context, start)?.let {
                    message = it
                    code = null
                }
            } catch (e: Exception) {
                busy = false
                message = "Couldn't reach the Antenna website: ${e.message}"
            }
        }
    }

    Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
        Column(Modifier.widthIn(max = 480.dp), verticalArrangement = Arrangement.spacedBy(16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            val licence = Licensing.licence(context)
            Text(when (status) {
                LicenceStatus.SignedOut -> "Sign in to Antenna"
                LicenceStatus.Expired -> if (licence?.plan == "trial" || licence?.plan == "none") "Your free trial has ended" else "Your plan has ended"
                LicenceStatus.Stale -> "Connect to the internet"
                LicenceStatus.Active -> ""
            }, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
            Text(when {
                code != null -> "Go to ${Licensing.client.linkPage} (it opens now), sign in or create an account, and enter this code. Antenna carries on by itself."
                status == LicenceStatus.SignedOut -> "Start your 7-day free trial, or use your plan, by signing in to your Antenna account. You'll get a code to enter on the Antenna website."
                status == LicenceStatus.Expired -> "Subscribe on the Antenna website to keep watching${licence?.email?.let { " ($it)" } ?: ""}. Once you've paid, tap Refresh."
                else -> "Antenna hasn't been able to check your account for a while. Connect to the internet and tap Refresh."
            }, color = Muted, textAlign = TextAlign.Center)
            code?.let {
                Text(it, fontSize = 40.sp, fontWeight = FontWeight.Bold, letterSpacing = 6.sp)
                CircularProgressIndicator()
                OutlinedButton(onClick = { openSite(context, Licensing.client.linkUrl(it)) }) { Text("Open the sign-in page") }
                TextButton(onClick = { waiting?.cancel(); code = null }) { Text("Cancel") }
            }
            message?.let { Text(it, color = Live, textAlign = TextAlign.Center) }
            if (code == null) when (status) {
                LicenceStatus.SignedOut -> Button(onClick = ::signIn, enabled = !busy, modifier = Modifier.fillMaxWidth()) { Text("Sign in") }
                LicenceStatus.Expired -> {
                    Button(onClick = { openSite(context, Licensing.client.pricingUrl) }, modifier = Modifier.fillMaxWidth()) { Text("Subscribe") }
                    OutlinedButton(onClick = { scope.launch { busy = true; Licensing.refresh(context); busy = false } }, enabled = !busy,
                        modifier = Modifier.fillMaxWidth()) { Text("Refresh") }
                    TextButton(onClick = { Licensing.signOut(context) }) { Text("Use another account") }
                }
                LicenceStatus.Stale -> {
                    Button(onClick = { scope.launch { busy = true; Licensing.refresh(context); busy = false } }, enabled = !busy,
                        modifier = Modifier.fillMaxWidth()) { Text("Refresh") }
                    TextButton(onClick = { Licensing.signOut(context) }) { Text("Sign out") }
                }
                LicenceStatus.Active -> Unit
            }
        }
    }
}
