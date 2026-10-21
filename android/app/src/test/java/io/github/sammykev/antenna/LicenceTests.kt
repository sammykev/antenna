package io.github.sammykev.antenna

import io.github.sammykev.antenna.licence.LicenceCheck
import io.github.sammykev.antenna.licence.LicenceStatus
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.Instant

/** licence-fixture.json is made by the site's own signing code (site/scripts/fixture.ts) with a test key. */
class LicenceTests {
    private val fixture = Json.parseToJsonElement(javaClass.getResourceAsStream("/licence-fixture.json")!!.readBytes().decodeToString()).jsonObject
    private fun f(name: String) = fixture[name]!!.jsonPrimitive.content

    @Test
    fun readsALicenceTheSiteSigned() {
        val licence = LicenceCheck.read(f("token"), f("publicKey"))
        assertNotNull(licence)
        assertEquals("user_test", licence!!.sub)
        assertEquals("monthly", licence.plan)
        assertEquals("device-fixture-1", licence.device)
    }

    @Test
    fun rejectsTamperingAndJunk() {
        assertNull(LicenceCheck.read(f("tampered"), f("publicKey")))
        assertNull(LicenceCheck.read("not.a.licence", f("publicKey")))
        assertNull(LicenceCheck.read(null, f("publicKey")))
    }

    @Test
    fun evaluatesDeviceDatesAndStaleness() {
        val l = LicenceCheck.read(f("token"), f("publicKey"))!!
        val issued = Instant.ofEpochSecond(l.iat)
        assertEquals(LicenceStatus.Active, LicenceCheck.evaluate(l, "device-fixture-1", issued.plusSeconds(86_400)))
        assertEquals(LicenceStatus.SignedOut, LicenceCheck.evaluate(l, "other", issued))
        assertEquals(LicenceStatus.Stale, LicenceCheck.evaluate(l, "device-fixture-1", Instant.ofEpochSecond(l.refresh)))
        assertEquals(LicenceStatus.Stale, LicenceCheck.evaluate(l, "device-fixture-1", issued.minusSeconds(30 * 86_400)))
        assertEquals(LicenceStatus.Expired, LicenceCheck.evaluate(l, "device-fixture-1", Instant.ofEpochSecond(l.until)))
    }
}
