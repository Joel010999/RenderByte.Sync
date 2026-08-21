namespace RenderByte.Sync.Agent.Configuration;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
