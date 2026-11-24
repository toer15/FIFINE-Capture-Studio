param([int]$Port = 9295, [int]$Seconds = 120)
# Mock host for UI testing only: accepts TCP connections on the console control port so the
# pairing dialog can be exercised without a real PS4/PS5. It sends and receives nothing.
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
Write-Output "mock console listening on 127.0.0.1:$Port"
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    if ($listener.Pending()) {
        $client = $listener.AcceptTcpClient()
        Write-Output "accepted connection from $($client.Client.RemoteEndPoint)"
        Start-Sleep -Milliseconds 300
        $client.Close()
    }
    Start-Sleep -Milliseconds 100
}
$listener.Stop()
