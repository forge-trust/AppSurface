using System.ComponentModel;
using System.Diagnostics;

/// <summary>Issues the history helper's read-only GitHub requests through the authenticated gh executable.</summary>
internal static class CoverageHistoryGh
{
    /// <summary>Returns the binary response to one GET within the remaining shared time budget.</summary>
    internal static async Task<byte[]> RequestAsync(string endpoint, int maxBytes, TimeSpan timeout)
    {
        // On Unix, env uses execvp's PATH lookup, as the Python helper did. A bare
        // ProcessStartInfo executable can also resolve beside the host or in the checkout.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("gh.exe")
            : new ProcessStartInfo("/usr/bin/env") { ArgumentList = { "gh" } };
        using var process = new Process { StartInfo = start };
        return await RequestAsync(process, endpoint, maxBytes, timeout);
    }

    /// <summary>Starts a caller-owned, unstarted process and waits for it to exit, discarding stderr and bounding stdout.</summary>
    internal static async Task<byte[]> RequestAsync(Process process, string endpoint, int maxBytes, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new IOException("GitHub API time budget exhausted.");
        }

        var start = process.StartInfo;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.ArgumentList.Add("api");
        start.ArgumentList.Add("--method");
        start.ArgumentList.Add("GET");
        start.ArgumentList.Add(endpoint);
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new IOException("Could not start gh.", ex);
        }

        // Drain stderr concurrently: a full pipe would otherwise stall gh while we read binary stdout.
        // Don't retain or relay its contents, which can include credential or server details.
        var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            using var response = new MemoryStream();
            var buffer = new byte[8192];
            var stdout = process.StandardOutput.BaseStream;
            int count;
            while ((count = await stdout.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (response.Length + count > maxBytes)
                {
                    throw new IOException("GitHub API response exceeded its size limit.");
                }

                response.Write(buffer, 0, count);
            }

            await drain;
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0)
            {
                throw new IOException("gh api request failed.");
            }

            return response.ToArray();
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            throw new IOException("GitHub API time budget exhausted.", ex);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // Natural exit raced with timeout or the stdout size check.
                }
            }

            await process.WaitForExitAsync();
            await deadline.CancelAsync();
            try
            {
                await drain;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Cancellation can interrupt the discarded stderr stream during cleanup.
            }
        }
    }
}
