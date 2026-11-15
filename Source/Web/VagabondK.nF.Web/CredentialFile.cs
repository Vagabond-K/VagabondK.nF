using System;
using System.IO;

namespace VagabondK.nF.Web
{
    /// <summary>
    /// <see cref="LoginGuard"/>의 자격 정보(비밀번호 해시·API 키)를 파일에 담고 읽습니다.
    /// </summary>
    /// <remarks>
    /// 파일 형식은 한 줄에 <c>h=&lt;해시&gt;</c>, <c>k=&lt;키&gt;</c>이며 값이 비면 생략합니다.
    /// 저장은 임시 파일에 쓴 뒤 본 파일을 지우고 이름을 바꾸는 방식이라, 전원 단절 시
    /// 반쪽 파일은 남지 않지만 타이밍에 따라 파일이 잠시 없을 수 있습니다(그 경우 false → 기본값).
    /// nanoFramework의 문자열 Split은 문자열 배열을 받으므로 개행 분해 시 "\n"을 넘깁니다.
    /// </remarks>
    public static class CredentialFile
    {
        /// <summary>
        /// 파일에서 해시·키를 읽어 가드에 적용합니다.
        /// </summary>
        /// <param name="guard">적용할 가드입니다.</param>
        /// <param name="path">절대경로입니다(예: "I:\\device.auth").</param>
        /// <returns>유효한 <c>h=</c>/<c>k=</c> 행을 하나라도 적용했으면 <see langword="true"/>.
        /// 파일이 없거나 읽기 실패/내용 없으면 <see langword="false"/> — 호출자가 기본값을 설정할 때 참고합니다.</returns>
        public static bool Load(LoginGuard guard, string path)
        {
            try
            {
                if (!File.Exists(path)) return false;

                string[] lines = File.ReadAllText(path).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("h=")) guard.PasswordHash = line.Substring(2);
                    else if (line.StartsWith("k=")) guard.ApiKey = line.Substring(2);
                }

                return (guard.PasswordHash != null && guard.PasswordHash.Length > 0) ||
                       (guard.ApiKey != null && guard.ApiKey.Length > 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 가드의 자격 정보를 임시 파일에 쓴 뒤 본 파일로 교체하는 방식으로 저장합니다(위 비고 참조).
        /// </summary>
        /// <param name="guard">저장할 가드입니다.</param>
        /// <param name="path">절대경로입니다. <c>path + ".tmp"</c>가 임시 파일로 쓰입니다.</param>
        /// <returns>성공 시 <see langword="null"/>, 실패 시 사유 문자열
        /// (<see cref="AuthController.Persist"/> 훅의 반환 규약과 동일).</returns>
        public static string Save(LoginGuard guard, string path)
        {
            try
            {
                string hash = guard.PasswordHash;
                string key = guard.ApiKey;
                string body = (hash != null && hash.Length > 0 ? "h=" + hash + "\r\n" : string.Empty) +
                              (key != null && key.Length > 0 ? "k=" + key + "\r\n" : string.Empty);

                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, body);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
