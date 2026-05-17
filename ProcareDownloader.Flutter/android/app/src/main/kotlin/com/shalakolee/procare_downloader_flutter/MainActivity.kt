package com.shalakolee.procare_downloader_flutter

import android.app.Activity
import android.content.ContentValues
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.documentfile.provider.DocumentFile
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    private val storageChannelName = "procare_downloader/storage"
    private val chooseDirectoryRequestCode = 4817
    private var pendingDirectoryResult: MethodChannel.Result? = null

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, storageChannelName).setMethodCallHandler { call, result ->
            when (call.method) {
                "chooseDirectory" -> chooseDirectory(result)
                "saveToCameraRoll" -> saveToCameraRoll(call.arguments as? Map<*, *>, result)
                "saveToTree" -> saveToTree(call.arguments as? Map<*, *>, result)
                else -> result.notImplemented()
            }
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != chooseDirectoryRequestCode) {
            return
        }

        val result = pendingDirectoryResult ?: return
        pendingDirectoryResult = null

        if (resultCode != Activity.RESULT_OK || data?.data == null) {
            result.success(null)
            return
        }

        val uri = data.data!!
        val flags = data.flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        try {
            contentResolver.takePersistableUriPermission(uri, flags)
        } catch (_: SecurityException) {
            // Some providers grant transient write access only. The selected URI can still be used now.
        }

        result.success(
            mapOf(
                "uri" to uri.toString(),
                "label" to labelForTree(uri),
            ),
        )
    }

    private fun chooseDirectory(result: MethodChannel.Result) {
        if (pendingDirectoryResult != null) {
            result.error("directory_picker_active", "A folder picker is already open.", null)
            return
        }

        pendingDirectoryResult = result
        val intent = Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            addFlags(Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            addFlags(Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION)
            addFlags(Intent.FLAG_GRANT_PREFIX_URI_PERMISSION)
        }
        startActivityForResult(intent, chooseDirectoryRequestCode)
    }

    private fun saveToCameraRoll(args: Map<*, *>?, result: MethodChannel.Result) {
        try {
            val bytes = args?.get("bytes") as? ByteArray ?: throw IllegalArgumentException("Missing image bytes.")
            val fileName = args["fileName"] as? String ?: throw IllegalArgumentException("Missing file name.")
            val relativePath = sanitizeRelativePath(args["relativePath"] as? String)
            val mimeType = args["mimeType"] as? String ?: "image/jpeg"
            val isVideo = mimeType.startsWith("video/")
            val baseDirectory = if (isVideo) Environment.DIRECTORY_MOVIES else Environment.DIRECTORY_PICTURES
            val collection = if (isVideo) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    MediaStore.Video.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
                } else {
                    MediaStore.Video.Media.EXTERNAL_CONTENT_URI
                }
            } else {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    MediaStore.Images.Media.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
                } else {
                    MediaStore.Images.Media.EXTERNAL_CONTENT_URI
                }
            }

            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, fileName)
                put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    put(MediaStore.MediaColumns.RELATIVE_PATH, joinMediaPath(baseDirectory, relativePath))
                    put(MediaStore.MediaColumns.IS_PENDING, 1)
                }
            }

            val uri = contentResolver.insert(collection, values)
                ?: throw IllegalStateException("Could not create media item.")
            contentResolver.openOutputStream(uri, "w")?.use { stream ->
                stream.write(bytes)
            } ?: throw IllegalStateException("Could not open media output stream.")

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                values.clear()
                values.put(MediaStore.MediaColumns.IS_PENDING, 0)
                contentResolver.update(uri, values, null, null)
            }

            result.success(uri.toString())
        } catch (error: Throwable) {
            result.error("save_camera_roll_failed", error.message, null)
        }
    }

    private fun saveToTree(args: Map<*, *>?, result: MethodChannel.Result) {
        try {
            val treeUri = Uri.parse(args?.get("treeUri") as? String ?: throw IllegalArgumentException("Missing folder URI."))
            val bytes = args["bytes"] as? ByteArray ?: throw IllegalArgumentException("Missing image bytes.")
            val fileName = args["fileName"] as? String ?: throw IllegalArgumentException("Missing file name.")
            val relativePath = sanitizeRelativePath(args["relativePath"] as? String)
            val mimeType = args["mimeType"] as? String ?: "image/jpeg"

            var directory = DocumentFile.fromTreeUri(this, treeUri)
                ?: throw IllegalArgumentException("Could not open selected folder.")

            relativePath.split('/').filter { it.isNotBlank() }.forEach { segment ->
                val existing = directory.findFile(segment)
                directory = if (existing?.isDirectory == true) {
                    existing
                } else {
                    directory.createDirectory(segment)
                        ?: throw IllegalStateException("Could not create folder $segment.")
                }
            }

            directory.findFile(fileName)?.delete()
            val file = directory.createFile(mimeType, fileName)
                ?: throw IllegalStateException("Could not create file.")
            contentResolver.openOutputStream(file.uri, "w")?.use { stream ->
                stream.write(bytes)
            } ?: throw IllegalStateException("Could not open output stream.")

            result.success(file.uri.toString())
        } catch (error: Throwable) {
            result.error("save_tree_failed", error.message, null)
        }
    }

    private fun labelForTree(uri: Uri): String {
        val raw = uri.lastPathSegment ?: return "Selected folder"
        return raw.substringAfter(':', raw).ifBlank { "Selected folder" }
    }

    private fun sanitizeRelativePath(value: String?): String {
        return value
            ?.split('/', '\\')
            ?.map { it.trim() }
            ?.filter { it.isNotEmpty() && it != "." && it != ".." }
            ?.joinToString("/")
            ?: ""
    }

    private fun joinMediaPath(baseDirectory: String, relativePath: String): String {
        return if (relativePath.isBlank()) baseDirectory else "$baseDirectory/$relativePath"
    }
}
