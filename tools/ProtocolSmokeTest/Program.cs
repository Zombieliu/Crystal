using System.Net.Sockets;
using System.Security.Cryptography;
using C = ClientPackets;
using S = ServerPackets;

Packet.IsServer = false;

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var clientExePath = Path.Combine(projectRoot, "Build", "Client", "Debug", "Client.exe");
var host = "127.0.0.1";
var port = 7000;
var password = "Test123";
var suffix = DateTime.Now.ToString("MMddHHmmss");
var accountId = $"cdx{suffix}"[..Math.Min(13, $"cdx{suffix}".Length)];
var characterName = $"Cdx{suffix}"[..Math.Min(13, $"Cdx{suffix}".Length)];

Console.WriteLine($"ProjectRoot: {projectRoot}");
Console.WriteLine($"ClientExe: {clientExePath}");
Console.WriteLine($"Server: {host}:{port}");
Console.WriteLine($"AccountID: {accountId}");
Console.WriteLine($"Character: {characterName}");

if (!File.Exists(clientExePath))
{
    Console.Error.WriteLine($"Client executable not found: {clientExePath}");
    return 2;
}

using var client = new TcpClient();
await client.ConnectAsync(host, port);
using var stream = client.GetStream();

var receiveBuffer = Array.Empty<byte>();

void Send(Packet packet)
{
    var data = packet.GetPacketBytes().ToArray();
    stream.Write(data, 0, data.Length);
    Console.WriteLine($"--> {packet.GetType().Name}");
}

void ReadFromStream()
{
    if (!stream.DataAvailable) return;

    var temp = new byte[16 * 1024];
    var bytesRead = stream.Read(temp, 0, temp.Length);
    if (bytesRead <= 0) throw new IOException("Connection closed by remote host.");

    var combined = new byte[receiveBuffer.Length + bytesRead];
    Buffer.BlockCopy(receiveBuffer, 0, combined, 0, receiveBuffer.Length);
    Buffer.BlockCopy(temp, 0, combined, receiveBuffer.Length, bytesRead);
    receiveBuffer = combined;
}

Packet? TryTakePacket()
{
    var packet = Packet.ReceivePacket(receiveBuffer, out var extra);
    if (packet == null) return null;

    receiveBuffer = extra;
    Console.WriteLine($"<-- {packet.GetType().Name}");

    if (packet is S.Disconnect disconnect)
        throw new InvalidOperationException($"Server disconnected. Reason={disconnect.Reason}");

    return packet;
}

Packet WaitForPacket(Func<Packet, bool> predicate, TimeSpan timeout, string description)
{
    var deadline = DateTime.UtcNow + timeout;

    while (DateTime.UtcNow < deadline)
    {
        ReadFromStream();

        Packet? packet;
        while ((packet = TryTakePacket()) != null)
        {
            if (predicate(packet))
                return packet;
        }

        Thread.Sleep(20);
    }

    throw new TimeoutException($"Timed out waiting for {description}.");
}

byte[] GetClientHash(string path)
{
    using var md5 = MD5.Create();
    using var file = File.OpenRead(path);
    return md5.ComputeHash(file);
}

WaitForPacket(packet => packet is S.Connected, TimeSpan.FromSeconds(5), "Connected");

Send(new C.ClientVersion { VersionHash = GetClientHash(clientExePath) });
var version = (S.ClientVersion)WaitForPacket(packet => packet is S.ClientVersion, TimeSpan.FromSeconds(10), "ClientVersion");
if (version.Result != 1)
    throw new InvalidOperationException($"Client version rejected. Result={version.Result}");

Send(new C.NewAccount
{
    AccountID = accountId,
    Password = password,
    BirthDate = new DateTime(2000, 1, 1),
    UserName = "Codex",
    SecretQuestion = "Q",
    SecretAnswer = "A",
    EMailAddress = $"{accountId}@local.test"
});

var newAccount = (S.NewAccount)WaitForPacket(packet => packet is S.NewAccount, TimeSpan.FromSeconds(10), "NewAccount");
if (newAccount.Result != 8)
    throw new InvalidOperationException($"Account creation failed. Result={newAccount.Result}");

Send(new C.Login { AccountID = accountId, Password = password });

var loginPacket = WaitForPacket(packet => packet is S.LoginSuccess || packet is S.Login || packet is S.LoginBanned, TimeSpan.FromSeconds(10), "Login");
if (loginPacket is S.Login loginFailure)
    throw new InvalidOperationException($"Login failed. Result={loginFailure.Result}");
if (loginPacket is S.LoginBanned loginBanned)
    throw new InvalidOperationException($"Login banned. Reason={loginBanned.Reason}, Expiry={loginBanned.ExpiryDate:O}");

var loginSuccess = (S.LoginSuccess)loginPacket;
Console.WriteLine($"Characters after login: {loginSuccess.Characters.Count}");

var selectedCharacter = loginSuccess.Characters.FirstOrDefault();
if (selectedCharacter == null)
{
    Send(new C.NewCharacter
    {
        Name = characterName,
        Gender = MirGender.Male,
        Class = MirClass.Warrior
    });

    var newCharacterPacket = WaitForPacket(packet => packet is S.NewCharacterSuccess || packet is S.NewCharacter, TimeSpan.FromSeconds(10), "NewCharacter");
    if (newCharacterPacket is S.NewCharacter newCharacterFailure)
        throw new InvalidOperationException($"Character creation failed. Result={newCharacterFailure.Result}");

    selectedCharacter = ((S.NewCharacterSuccess)newCharacterPacket).CharInfo;
    Console.WriteLine($"Created character: {selectedCharacter.Name} (Index={selectedCharacter.Index})");
}
else
{
    Console.WriteLine($"Using existing character: {selectedCharacter.Name} (Index={selectedCharacter.Index})");
}

Send(new C.StartGame { CharacterIndex = selectedCharacter.Index });

S.UserInformation? currentUser = null;
S.MapInformation? currentMap = null;
var gameStarted = false;
var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

while (DateTime.UtcNow < startDeadline && (!gameStarted || currentUser == null || currentMap == null))
{
    ReadFromStream();

    Packet? packet;
    while ((packet = TryTakePacket()) != null)
    {
        switch (packet)
        {
            case S.StartGame startGame when startGame.Result == 4:
                gameStarted = true;
                Console.WriteLine($"StartGame accepted. Resolution={startGame.Resolution}");
                break;
            case S.StartGame startGame:
                throw new InvalidOperationException($"StartGame failed. Result={startGame.Result}");
            case S.StartGameDelay delay:
                Console.WriteLine($"StartGame delayed by {delay.Milliseconds}ms, retrying.");
                Thread.Sleep((int)Math.Min(delay.Milliseconds + 200, 5000));
                Send(new C.StartGame { CharacterIndex = selectedCharacter.Index });
                break;
            case S.UserInformation userInformation:
                currentUser = userInformation;
                Console.WriteLine($"UserInformation received. ObjectID={userInformation.ObjectID}, Location={userInformation.Location}");
                break;
            case S.MapInformation mapInformation:
                currentMap = mapInformation;
                Console.WriteLine($"MapInformation received. Map={mapInformation.Title} ({mapInformation.FileName})");
                break;
        }
    }

    Thread.Sleep(20);
}

if (!gameStarted)
    throw new TimeoutException("Did not receive successful StartGame packet.");
if (currentUser == null)
    throw new TimeoutException("Did not receive UserInformation after StartGame.");
if (currentMap == null)
    throw new TimeoutException("Did not receive MapInformation after StartGame.");

Send(new C.LogOut());
var logOutPacket = WaitForPacket(packet => packet is S.LogOutSuccess || packet is S.LogOutFailed, TimeSpan.FromSeconds(3), "LogOut");
if (logOutPacket is S.LogOutFailed)
{
    Console.WriteLine("Immediate logout denied, waiting for logout delay.");
    Thread.Sleep(10500);
    Send(new C.LogOut());
    logOutPacket = WaitForPacket(packet => packet is S.LogOutSuccess || packet is S.LogOutFailed, TimeSpan.FromSeconds(5), "LogOut retry");
}
if (logOutPacket is S.LogOutFailed)
    throw new InvalidOperationException("LogOut failed after waiting for delay.");

var loggedOut = (S.LogOutSuccess)logOutPacket;
Console.WriteLine($"Returned to select scene. Characters={loggedOut.Characters.Count}");

SelectInfo secondCharacter;
if (loggedOut.Characters.Count < 2)
{
    var secondCharacterName = $"C2{suffix}"[..Math.Min(13, $"C2{suffix}".Length)];
    Send(new C.NewCharacter
    {
        Name = secondCharacterName,
        Gender = MirGender.Female,
        Class = MirClass.Wizard
    });

    var secondCharacterPacket = WaitForPacket(packet => packet is S.NewCharacterSuccess || packet is S.NewCharacter, TimeSpan.FromSeconds(10), "Second NewCharacter");
    if (secondCharacterPacket is S.NewCharacter secondCharacterFailure)
        throw new InvalidOperationException($"Second character creation failed. Result={secondCharacterFailure.Result}");

    secondCharacter = ((S.NewCharacterSuccess)secondCharacterPacket).CharInfo;
    Console.WriteLine($"Created second character: {secondCharacter.Name} (Index={secondCharacter.Index})");
}
else
{
    secondCharacter = loggedOut.Characters
        .First(character => character.Index != selectedCharacter.Index);
    Console.WriteLine($"Using existing second character: {secondCharacter.Name} (Index={secondCharacter.Index})");
}

Send(new C.StartGame { CharacterIndex = secondCharacter.Index });

currentUser = null;
currentMap = null;
gameStarted = false;
startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

while (DateTime.UtcNow < startDeadline && (!gameStarted || currentUser == null || currentMap == null))
{
    ReadFromStream();

    Packet? packet;
    while ((packet = TryTakePacket()) != null)
    {
        switch (packet)
        {
            case S.StartGame startGame when startGame.Result == 4:
                gameStarted = true;
                Console.WriteLine($"Second StartGame accepted. Resolution={startGame.Resolution}");
                break;
            case S.StartGame startGame:
                throw new InvalidOperationException($"Second StartGame failed. Result={startGame.Result}");
            case S.StartGameDelay delay:
                Console.WriteLine($"Second StartGame delayed by {delay.Milliseconds}ms, retrying.");
                Thread.Sleep((int)Math.Min(delay.Milliseconds + 200, 5000));
                Send(new C.StartGame { CharacterIndex = secondCharacter.Index });
                break;
            case S.UserInformation userInformation:
                currentUser = userInformation;
                Console.WriteLine($"Second UserInformation received. ObjectID={userInformation.ObjectID}, Location={userInformation.Location}");
                break;
            case S.MapInformation mapInformation:
                currentMap = mapInformation;
                Console.WriteLine($"Second MapInformation received. Map={mapInformation.Title} ({mapInformation.FileName})");
                break;
        }
    }

    Thread.Sleep(20);
}

if (!gameStarted || currentUser == null || currentMap == null)
    throw new TimeoutException("Did not fully enter game with second character.");

var chatMessage = $"smoke-{suffix}";
Send(new C.Chat { Message = chatMessage });
var chatPacket = WaitForPacket(
    packet => packet is S.Chat chat && chat.Message.Contains(chatMessage, StringComparison.Ordinal) ||
              packet is S.ObjectChat objectChat && objectChat.Text.Contains(chatMessage, StringComparison.Ordinal),
    TimeSpan.FromSeconds(5),
    "Chat echo");

switch (chatPacket)
{
    case S.Chat chat:
        Console.WriteLine($"Chat received: {chat.Message}");
        break;
    case S.ObjectChat objectChat:
        Console.WriteLine($"ObjectChat received: {objectChat.Text}");
        break;
}

var originalLocation = currentUser.Location;
Send(new C.Walk { Direction = MirDirection.Right });
var walkPacket = (S.UserLocation)WaitForPacket(packet => packet is S.UserLocation, TimeSpan.FromSeconds(5), "UserLocation after walk");
Console.WriteLine($"Walk processed. Location {originalLocation} -> {walkPacket.Location}, Direction={walkPacket.Direction}");

Console.WriteLine("Smoke test passed.");
Console.WriteLine($"AccountID={accountId}");
Console.WriteLine($"Password={password}");
Console.WriteLine($"Character1={selectedCharacter.Name}");
Console.WriteLine($"Character2={secondCharacter.Name}");

Send(new C.Disconnect());
return 0;
