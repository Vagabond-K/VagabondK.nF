using VagabondK.nF.MMS;
namespace VagabondK.nF.IEC61850.Client
{
    /// <summary>IEC 61850 DataSet으로 사용하는 MMS 명명 변수 목록의 고정 스냅샷입니다.</summary>
    public sealed class MmsDataSet : MmsNamedVariableList
    {
        /// <summary>구성 시 한 번 계산한 domain/item 보고 참조 이름입니다.</summary>
        public string Reference { get; private set; }
        internal MmsDataSet(MmsNamedVariableList list)
            : base(list.Domain, list.Item, list.Deletable, CopyMembers(list))
        { Reference = Domain + "/" + Item; }
        private static MmsVariableReference[] CopyMembers(MmsNamedVariableList list)
        {
            var members = new MmsVariableReference[list.Count];
            for (int i = 0; i < members.Length; i++) members[i] = list.GetMember(i);
            return members;
        }
    }
    /// <summary>MMS 명명 변수 목록을 IEC 61850 DataSet 디렉터리와 읽기 결과로 제공합니다.</summary>
    public sealed partial class Iec61850Client
    {
        /// <summary>Domain/변수 또는 IEC 61850 DataSet 이름의 한 페이지를 조회합니다.</summary>
        /// <param name="domain">null이면 domain 목록, 지정하면 해당 domain의 변수 또는 DataSet 목록입니다.</param>
        /// <param name="continueAfter">직전 페이지의 마지막 이름입니다. 첫 페이지는 null입니다.</param>
        /// <param name="maximumNames">한 응답에서 허용할 이름 수 상한(1~128)입니다.</param>
        /// <param name="dataSets">true이면 domain 내의 명명 변수 목록 이름을 조회합니다.</param>
        /// <returns>이번 페이지의 이름과 다음 페이지 조회용 커서입니다.</returns>
        /// <exception cref="System.ArgumentException">식별자가 잘못되었거나 domain 없이 DataSet 목록을 요청했습니다.</exception>
        /// <exception cref="System.InvalidOperationException">목록 서비스가 협상되지 않았거나 연결을 사용할 수 없습니다.</exception>
        /// <remarks>maximumNames는 서버 페이지 크기를 지시하지 않습니다. 응답이 이 상한을 넘으면 프로토콜 예외/Faulted입니다.</remarks>
        public new MmsNameListPage GetNameList(string domain = null, string continueAfter = null,
            int maximumNames = 128, bool dataSets = false)
        { return base.GetNameList(domain, continueAfter, maximumNames, dataSets); }
        /// <summary>DataSet 디렉터리 서비스 광고 여부입니다.</summary>
        public bool SupportsDataSetDirectory { get { return SupportsNamedVariableListDirectory; } }
        /// <summary>MMS 명명 변수 목록을 IEC 61850 DataSet으로 조회합니다.</summary>
        /// <param name="domain">DataSet이 속한 MMS domain 식별자입니다.</param>
        /// <param name="item">DataSet의 MMS item 식별자입니다.</param>
        /// <returns>최대 16개 참조와 고정 보고 참조 이름을 보관하는 스냅샷입니다.</returns>
        /// <exception cref="System.InvalidOperationException">디렉터리 서비스가 협상되지 않았거나 연결을 사용할 수 없습니다.</exception>
        public MmsDataSet GetDataSetDirectory(string domain, string item)
        { return new MmsDataSet(GetNamedVariableListDirectory(domain, item)); }
        /// <summary>DataSet의 참조와 응답 개수를 검사하여 값을 읽습니다.</summary>
        /// <param name="dataSet">앞서 조회한 DataSet 디렉터리입니다.</param>
        /// <returns>디렉터리 순서로 정렬된 값 스냅샷과 항목별 접근 오류입니다.</returns>
        /// <exception cref="System.ArgumentNullException">DataSet 디렉터리가 null입니다.</exception>
        public MmsReadResult[] ReadDataSet(MmsDataSet dataSet) { return ReadNamedVariableList(dataSet); }
    }
}