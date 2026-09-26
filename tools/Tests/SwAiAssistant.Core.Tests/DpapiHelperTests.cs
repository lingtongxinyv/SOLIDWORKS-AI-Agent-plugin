using SwAiAssistant.Core.Security;
using Xunit;

namespace SwAiAssistant.Core.Tests
{
    public class DpapiHelperTests
    {
        [Fact]
        public void RoundTrip_ReturnsOriginalPlainText()
        {
            const string plain = "sk-secret-明文-!@#$%^&*()_+-=";

            string cipher = DpapiHelper.Protect(plain);
            string back = DpapiHelper.Unprotect(cipher);

            Assert.Equal(plain, back);
        }

        [Fact]
        public void Protect_OutputIsNotPlainText()
        {
            const string plain = "sk-AAAABBBBCCCC1234567890";

            string cipher = DpapiHelper.Protect(plain);

            Assert.NotEqual(plain, cipher);
            Assert.DoesNotContain(plain, cipher);
        }

        [Fact]
        public void EmptyString_PassesThrough()
        {
            Assert.Equal(string.Empty, DpapiHelper.Protect(string.Empty));
            Assert.Equal(string.Empty, DpapiHelper.Unprotect(string.Empty));
        }
    }
}
