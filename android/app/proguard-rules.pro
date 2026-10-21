# libVLC is called from its native code by name (JNI callbacks, events): keep it whole.
-keep class org.videolan.libvlc.** { *; }
-keep interface org.videolan.libvlc.** { *; }
-keep class org.videolan.medialibrary.** { *; }
# The sync file's JSON classes are (de)serialised through generated serializers.
-keepclassmembers class io.github.sammykev.antenna.sync.SyncService$* { *** Companion; }
-keep,includedescriptorclasses class io.github.sammykev.antenna.sync.**$$serializer { *; }
