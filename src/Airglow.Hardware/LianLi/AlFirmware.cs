namespace Airglow.Hardware.LianLi;

public sealed record AlFirmware(byte CustomerId, byte ProjectId, byte MajorId, byte MinorId, byte FineTune)
{
    public bool IsValid => CustomerId == 0xE0 && ProjectId == 0x50 && MajorId == 0x80 && MinorId == 0xC3;

    public string Version
    {
        get
        {
            if (FineTune < 8)
            {
                return "1.0";
            }

            var n = (FineTune >> 4) * 10 + (FineTune & 0x0F) + 2;
            return $"{n / 10}.{n % 10}";
        }
    }
}
