package io.github.sammykev.antenna

import java.io.Closeable
import java.io.OutputStream
import java.net.ServerSocket
import kotlin.concurrent.thread

/** A minimal local HTTP server for tests (the JDK's isn't on Android's unit-test classpath). Routes match path + query. */
class TestHttpServer : Closeable {
    private val routes = mutableMapOf<String, Pair<Int, (OutputStream) -> Long>>()
    private val socket = ServerSocket(0)
    val base = "http://127.0.0.1:${socket.localPort}"
    @Volatile var lastUserAgent: String? = null

    fun route(path: String, status: Int = 200, body: ByteArray = ByteArray(0)) {
        routes[path] = status to { out -> out.write(body); body.size.toLong() }
    }

    /** A body written in pieces, without holding it all in memory (chunked). */
    fun stream(path: String, write: (OutputStream) -> Unit) {
        routes[path] = 200 to { out -> write(out); -1L }
    }

    fun start() = thread(isDaemon = true) {
        while (!socket.isClosed) {
            val client = runCatching { socket.accept() }.getOrNull() ?: break
            thread(isDaemon = true) {
                client.use { c ->
                    val reader = c.getInputStream().bufferedReader()
                    val target = reader.readLine()?.split(' ')?.getOrNull(1) ?: return@use
                    while (true) {
                        val line = reader.readLine() ?: break
                        if (line.isEmpty()) break
                        if (line.startsWith("User-Agent:", true)) lastUserAgent = line.substringAfter(':').trim()
                    }
                    val (status, body) = routes[target] ?: routes[target.substringBefore('?')] ?: (404 to { _: OutputStream -> 0L })
                    val out = c.getOutputStream().buffered()
                    // No length: the body runs until the connection closes, so big ones can be written in pieces.
                    out.write("HTTP/1.1 $status X\r\nConnection: close\r\n\r\n".toByteArray())
                    body(out)
                    out.flush()
                }
            }
        }
    }

    override fun close() = socket.close()
}
