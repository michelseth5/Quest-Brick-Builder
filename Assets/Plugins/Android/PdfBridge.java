package com.michelseth5.brickbuilder;

import android.app.Activity;
import android.app.Fragment;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.pdf.PdfRenderer;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.os.ParcelFileDescriptor;
import android.provider.Settings;

import com.unity3d.player.UnityPlayer;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Instruction book support for the game:
 *  - pickPdf: opens Android's file picker (e.g. the Quest's Downloads) and copies the chosen PDF
 *    into the app's own storage, then tells Unity where it went.
 *  - pageCount / renderPageAsync: draws PDF pages to JPEG files with Android's built-in PdfRenderer.
 * Results go back to Unity with UnitySendMessage(receiver, method, text).
 */
public class PdfBridge {
    private static final int PICK_REQUEST = 4242;

    public static void pickPdf(final Activity activity, final String destinationPath,
                               final String receiver, final String method) {
        activity.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                PickerFragment fragment = new PickerFragment();
                Bundle args = new Bundle();
                args.putString("destination", destinationPath);
                args.putString("receiver", receiver);
                args.putString("method", method);
                fragment.setArguments(args);
                activity.getFragmentManager().beginTransaction().add(fragment, "pdf-picker").commit();
            }
        });
    }

    /** An invisible helper that starts the picker and receives its result. */
    public static class PickerFragment extends Fragment {
        @Override
        public void onCreate(Bundle savedInstanceState) {
            super.onCreate(savedInstanceState);
            Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
            intent.addCategory(Intent.CATEGORY_OPENABLE);
            intent.setType("application/pdf");
            startActivityForResult(intent, PICK_REQUEST);
        }

        @Override
        public void onActivityResult(int requestCode, int resultCode, Intent data) {
            Bundle args = getArguments();
            String outcome = "";
            if (requestCode == PICK_REQUEST && resultCode == Activity.RESULT_OK && data != null && data.getData() != null) {
                String destination = args.getString("destination");
                InputStream in = null;
                OutputStream out = null;
                try {
                    new File(destination).getParentFile().mkdirs();
                    in = getActivity().getContentResolver().openInputStream(data.getData());
                    out = new FileOutputStream(destination);
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while ((read = in.read(buffer)) > 0) {
                        out.write(buffer, 0, read);
                    }
                    outcome = destination;
                } catch (Exception e) {
                    outcome = "error:" + e.getMessage();
                } finally {
                    try { if (in != null) in.close(); } catch (Exception ignored) { }
                    try { if (out != null) out.close(); } catch (Exception ignored) { }
                }
            }
            UnityPlayer.UnitySendMessage(args.getString("receiver"), args.getString("method"), outcome);
            getFragmentManager().beginTransaction().remove(this).commit();
        }
    }

    /** True if the app may read the shared Downloads folder directly ("All files access"). */
    public static boolean hasFileAccess() {
        if (Build.VERSION.SDK_INT >= 30) {
            return Environment.isExternalStorageManager();
        }
        return true;
    }

    /** Opens the system page where the player can switch on "All files access" for this app. */
    public static void requestFileAccess(final Activity activity) {
        try {
            Intent intent = new Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                                       Uri.parse("package:" + activity.getPackageName()));
            activity.startActivity(intent);
        } catch (Exception e) {
            try {
                activity.startActivity(new Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION));
            } catch (Exception ignored) {
            }
        }
    }

    public static String downloadsFolder() {
        return Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS).getAbsolutePath();
    }

    public static int pageCount(String path) {
        ParcelFileDescriptor descriptor = null;
        PdfRenderer renderer = null;
        try {
            descriptor = ParcelFileDescriptor.open(new File(path), ParcelFileDescriptor.MODE_READ_ONLY);
            renderer = new PdfRenderer(descriptor);
            return renderer.getPageCount();
        } catch (Exception e) {
            return 0;
        } finally {
            try { if (renderer != null) renderer.close(); } catch (Exception ignored) { }
            try { if (descriptor != null) descriptor.close(); } catch (Exception ignored) { }
        }
    }

    /** Draws one page (0-based) at the given pixel width to a JPEG, on a background thread. Replies "page|path" or "page|error:...". */
    public static void renderPageAsync(final String path, final int page, final int width, final String outputPath,
                                       final String receiver, final String method) {
        new Thread(new Runnable() {
            @Override
            public void run() {
                String reply;
                ParcelFileDescriptor descriptor = null;
                PdfRenderer renderer = null;
                try {
                    descriptor = ParcelFileDescriptor.open(new File(path), ParcelFileDescriptor.MODE_READ_ONLY);
                    renderer = new PdfRenderer(descriptor);
                    PdfRenderer.Page pdfPage = renderer.openPage(page);
                    int height = Math.round(width * (float) pdfPage.getHeight() / pdfPage.getWidth());
                    Bitmap bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
                    bitmap.eraseColor(Color.WHITE);
                    pdfPage.render(bitmap, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY);
                    pdfPage.close();

                    new File(outputPath).getParentFile().mkdirs();
                    FileOutputStream out = new FileOutputStream(outputPath);
                    bitmap.compress(Bitmap.CompressFormat.JPEG, 88, out);
                    out.close();
                    bitmap.recycle();
                    reply = page + "|" + outputPath;
                } catch (Exception e) {
                    reply = page + "|error:" + e.getMessage();
                } finally {
                    try { if (renderer != null) renderer.close(); } catch (Exception ignored) { }
                    try { if (descriptor != null) descriptor.close(); } catch (Exception ignored) { }
                }
                UnityPlayer.UnitySendMessage(receiver, method, reply);
            }
        }).start();
    }
}
